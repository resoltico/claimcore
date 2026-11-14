module ClaimCore.Tests.CaseDecisionTests

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.TestSupport

open System
open Expecto
open ClaimCore.Json
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures


let private replay () =
    let request, entry, current, receipt = created 10 "PURE-CASE"
    let view = locked worker (Some current) (Some entry)

    Expect.equal
        (decide request view)
        (Verdict.Replay receipt)
        "Replay retains receipt and executor"

    for changed in
        [
            { request with
                CaseReference = "pure-case"
            }
            { request with
                Act =
                    AuthoredCaseAct.Open(
                        ClaimCore.TestSupport.Inputs.registrationWith
                            [ BusinessField.ClaimedAmount, ("1000") ]
                            (registration)
                    )
            }
        ] do
        Expect.equal
            (decide changed view)
            (Verdict.Replay receipt)
            "Equivalent amount and reference spellings retain the original receipt"

    let later =
        { current with
            Snapshot =
                { current.Snapshot with
                    Revision = CaseFixtures.revision 2L
                }
        }

    let laterView = locked worker (Some later) (Some entry)

    Expect.equal
        (decide request laterView)
        (Verdict.Replay receipt)
        "Replay precedes current revision checks"

    match
        CaseOperations.decide
            SubjectExecutionMode.Preview
            (DecisionFixtures.services installation calendar owner)
            (CaseFixtures.command request)
            laterView.Read
    with
    | Verdict.Replay accepted -> Expect.equal accepted receipt "Preview observes exact acceptance"
    | _ -> failtest "Preview must find the accepted identity"

let private authorization () =
    let request, entry, current, _ = created 11 "PURE-RIGHTS"
    let revoked = actor owner ((CaseFixtures.actorId (id 1))) [ Role.Owner ] []

    Expect.equal
        (decide request (locked revoked (Some current) (Some entry)))
        (Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Revocation precedes identity disclosure"

    let scoped =
        actor owner ((CaseFixtures.actorId (id 1))) [] [ current.CaseId, [ Role.CaseHandler ] ]

    Expect.equal
        (decide (opening 12 "NEW-CASE") (locked scoped None None))
        (Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Case handling cannot create another resource"

    let disabled =
        { worker with
            Actor = worker.Actor |> Option.map (fun value -> { value with Enabled = false })
        }

    Expect.equal
        (decide request (locked disabled (Some current) (Some entry)))
        (Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Disabled actor cannot replay"

let private reviewAndTime () =
    let request = opening 13 "PURE-REVIEW"
    let view = locked worker None None

    match
        CaseOperations.decide
            SubjectExecutionMode.Preview
            (DecisionFixtures.services installation calendar owner)
            (CaseFixtures.command request)
            view.Read
    with
    | Verdict.Proceed(None, after) ->
        Expect.equal
            after.After.Revision
            (CaseFixtures.revision (1L))
            "Pure review proposes without append"
    | _ -> failtest "Expected a pure proposed state"

    let early =
        { view with
            Read =
                { view.Read with
                    ObservedAt = DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)
                }
        }

    Expect.equal
        (decide
            { request with
                OperationId = OperationIds.numberedAt early.Read.ObservedAt 13
            }
            early)
        (Verdict.Refuse(
            Refusal.invalidInput (
                InputTarget.Field BusinessField.IncidentNotificationDate,
                Violation.FutureDate
            )
        ))
        "Supplied snapshot instant owns the business date"

    let _, _, current, _ = created 14 "PURE-REVIEW-CURRENT"

    Expect.equal
        (decide
            (opening 13 (CaseReference.format current.Snapshot.Reference))
            (locked worker (Some current) None))
        (Verdict.Refuse(Refusal.Case CaseRefusal.CaseAlreadyExists))
        "An earlier preview cannot authorize opening after another acceptance"

    let stale =
        actRequest
            15
            (CaseReference.format current.Snapshot.Reference)
            0L
            (AuthoredCaseAct.Bare BareAct.Close)

    Expect.equal
        (decide stale (locked worker (Some current) None))
        (Verdict.Refuse(Refusal.revisionConflict (CaseFixtures.revision 1L)))
        "Execution rechecks the current revision"

let private integrityAndPrivacy () =
    let original, entry, _, _ = created 16 "PURE-IDENTITY"

    Expect.equal
        (OperationRoot.restore
            entry.Root.Header
            (OperationRoot.commandBytes entry.Root)
            (OperationRoot.effectBytes entry.Root)
            entry.Root.Structure.Compartments
            EntryHash.zero)
        (Error OperationRootProblem.Hash)
        "Corrupt stored proof cannot enter the typed decision pipeline"

    let _, _, readable, _ = created 17 "READABLE-CASE"

    let scoped =
        actor owner ((CaseFixtures.actorId (id 1))) [] [ readable.CaseId, [ Role.CaseHandler ] ]

    let different =
        { original with
            CaseReference = (CaseReference.format readable.Snapshot.Reference)
            ExpectedRevision = 1L
            Act = (AuthoredCaseAct.Bare BareAct.Close)
        }

    Expect.equal
        (decide different (locked scoped (Some readable) (Some entry)))
        (Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable))
        "An inaccessible existing operation does not leak through another readable case"

let private bounds () =
    let _, _, current, _ = created 18 "PURE-BOUND"

    let full =
        { locked worker None None with
            Head =
                { head with
                    Sequence = CaseFixtures.sequence 9007199254740991L
                }
        }

    Expect.equal
        (decide (opening 19 "BOUND-LEDGER") full)
        (Verdict.Fail CoreFault.InternalError)
        "An unrepresentable ledger successor is an internal fault"

    let maxRevision =
        { current with
            Snapshot =
                { current.Snapshot with
                    Revision = CaseFixtures.revision 9007199254740991L
                }
        }

    Expect.equal
        (decide
            (actRequest
                21
                (CaseReference.format current.Snapshot.Reference)
                Revision.Maximum
                (AuthoredCaseAct.Bare BareAct.Close))
            (locked worker (Some maxRevision) None))
        (Verdict.Fail CoreFault.InternalError)
        "A format-valid record at the bound yields CORE_INTERNAL_ERROR"

let private opening =
    (OperationSubmissionCodecs.submission.Write
     << ClaimCore.TestSupport.Submissions.ofCommand None) (
        ClaimCore.TestSupport.Inputs.caseOperation
            {
                OperationId = id 10
                CaseReference = "UNIT-001"
                ExpectedRevision = 0L
                Act = AuthoredCaseAct.Open Inputs.registration
            }
    )

let private rawOpening = opening |> WireWriter.bytes |> Text.Encoding.UTF8.GetString

let rec private reverseMembers =
    function
    | JsonValue.Object members ->
        members
        |> List.rev
        |> List.map (fun (key, value) -> key, reverseMembers value)
        |> JsonValue.Object
    | JsonValue.Array values -> JsonValue.Array(List.map reverseMembers values)
    | value -> value

let private respelledOpening =
    opening |> reverseMembers |> WireWriter.bytes |> Text.Encoding.UTF8.GetString

let private decodeWire (text: string) =
    Codec.decode
        Limits.MaximumRequestBytes
        Api.operationExecute.Input
        (Text.Encoding.UTF8.GetBytes text)
    |> Result.map (
        Submissions.command
        >> function
            | OperationInput.Case value -> Inputs.referencedCase value
            | _ -> failtest "Expected a case execution request"
    )
    |> Result.defaultWith (fun _ -> failtest "Synthetic raw request must decode")

let private wireSpelling () =
    let original = decodeWire rawOpening
    let view = locked worker None None
    let draft, projection, receipt = decide original view |> appended

    let current =
        {
            StoredCase.CaseId = projection.Rows.Snapshot.CaseId
            Snapshot = projection.Rows.Snapshot
            Index = projection.Index
            SnapshotBytes =
                ClaimCore.RecordFormat.CaseRecord.encodeSnapshot projection.Rows.Snapshot
        }

    let replayView = locked worker (Some current) (Some draft)

    let later =
        { replayView with
            Read =
                { replayView.Read with
                    ObservedAt = instant.AddMinutes 1.
                }
        }

    let historical (receipt: OperationReceipt) =
        receipt.OperationId,
        receipt.Position,
        receipt.Subject,
        receipt.RecordedAt,
        receipt.RecordedBy

    for spelling in [ respelledOpening; respelledOpening.Replace("1000.00", "1000.0") ] do
        match decide (decodeWire spelling) later with
        | Verdict.Replay replay ->
            Expect.equal
                (historical replay)
                (historical receipt)
                "Re-spelling retains every historical receipt field, executor and accepting instant"

            Expect.equal
                replay.Basis
                {
                    ObservedAt = DateTimeOffset(2026, 9, 7, 12, 1, 0, TimeSpan.Zero)
                    BusinessDate = DateOnly(2026, 9, 7)
                    TimeZoneId = "Etc/UTC"
                }
                "Replay reports its actual later observation independently of the accepted event"
        | other -> failtestf "Equivalent spelling must replay the accepted operation: %A" other

    Expect.equal
        (decide (decodeWire (respelledOpening.Replace("1000.00", "1000.01"))) later)
        (Verdict.Refuse Refusal.IdempotencyConflict)
        "A different amount still conflicts"

let private terminalRevisionBoundary () =
    let _, entry, _, _ = created 21 "TERMINAL-REQUEST"

    let scope =
        match entry.Root.Structure.Command with
        | CommandSkeleton.Case scope -> { scope with Act = CaseActKind.Close }
        | _ -> failtest "Case root required"

    let check revision =
        OperationStructure.expected
            entry.Root.Header
            (CommandSkeleton.Case
                { scope with
                    ExpectedRevision = CaseFixtures.revision revision
                })

    Expect.isOk
        (check (Revision.Maximum - 1L))
        "The last representable successor is a valid control"

    Expect.equal
        (check Revision.Maximum)
        (Error OperationStructureProblem.Command)
        "No accepted root can claim a successor beyond the permanent revision boundary"

[<Tests>]
let tests =
    testList
        "pure case decisions"
        [
            testCase
                "terminal expected revisions cannot construct an accepted root"
                terminalRevisionBoundary
            testCase "raw JSON spelling preserves the accepted receipt" wireSpelling
            testCase "canonical replay preserves attribution and original identity" replay
            testCase "current resource access precedes replay and creation" authorization
            testCase "review business date and revision use the supplied view" reviewAndTime
            testCase
                "corruption faults and foreign identity privacy remain distinct"
                integrityAndPrivacy
            testCase
                "unrepresentable successors are internal faults and fences prevent append"
                bounds
        ]
