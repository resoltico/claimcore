module ClaimCore.Tests.CaseDecisionTests

open ClaimCore.TestSupport

open System
open Expecto
open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures

let private replay () =
    let request, entry, current, receipt = created 10 "PURE-CASE"
    let view = locked worker (Some current) (Some entry)

    Expect.equal
        (decide request view)
        (Decision.Replay receipt)
        "Replay retains receipt and executor"

    for changed in
        [
            { request with
                CaseReference = "pure-case"
            }
            { request with
                Act =
                    UnvalidatedCaseAct.Open
                        { registration with
                            ClaimedAmount = "1000"
                        }
            }
        ] do
        Expect.equal
            (decide changed view)
            (Decision.Replay receipt)
            "Equivalent amount and reference spellings retain the original receipt"

    let later =
        { current with
            Snapshot = { current.Snapshot with Revision = 2L }
        }

    let laterView = locked worker (Some later) (Some entry)

    Expect.equal
        (decide request laterView)
        (Decision.Replay receipt)
        "Replay precedes current revision checks"

    match
        CaseDecisions.review
            installation
            calendar
            owner
            (CaseFixtures.command request)
            laterView.Read
    with
    | Decision.Replay accepted -> Expect.equal accepted receipt "Preview observes exact acceptance"
    | _ -> failtest "Preview must find the accepted identity"

let private authorization () =
    let request, entry, current, _ = created 11 "PURE-RIGHTS"
    let revoked = actor owner (id 1) [ Role.Owner ] []

    Expect.equal
        (decide request (locked revoked (Some current) (Some entry)))
        (Decision.Refuse Refusal.ResourceUnavailable)
        "Revocation precedes identity disclosure"

    let scoped = actor owner (id 1) [] [ current.CaseId, [ Role.CaseHandler ] ]

    Expect.equal
        (decide (opening 12 "NEW-CASE") (locked scoped None None))
        (Decision.Refuse Refusal.ResourceUnavailable)
        "Case handling cannot create another resource"

    let disabled =
        { worker with
            Actor = worker.Actor |> Option.map (fun value -> { value with Enabled = false })
        }

    Expect.equal
        (decide request (locked disabled (Some current) (Some entry)))
        (Decision.Refuse Refusal.ResourceUnavailable)
        "Disabled actor cannot replay"

let private reviewAndTime () =
    let request = opening 13 "PURE-REVIEW"
    let view = locked worker None None

    match
        CaseDecisions.review installation calendar owner (CaseFixtures.command request) view.Read
    with
    | Decision.Proceed(None, after) ->
        Expect.equal (Case.snapshot after).Revision 1L "Pure review proposes without append"
    | _ -> failtest "Expected a pure proposed state"

    let early =
        { view with
            Read =
                { view.Read with
                    ObservedAt = DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)
                }
        }

    Expect.equal
        (decide request early)
        (Decision.Refuse(
            Refusal.InvalidInput(
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
        (Decision.Refuse Refusal.CaseAlreadyExists)
        "An earlier preview cannot authorize opening after another acceptance"

    let stale =
        actRequest 15 (CaseReference.format current.Snapshot.Reference) 0L UnvalidatedCaseAct.Close

    Expect.equal
        (decide stale (locked worker (Some current) None))
        (Decision.Refuse(Refusal.RevisionConflict 1L))
        "Execution rechecks the current revision"

let private integrityAndPrivacy () =
    let original, entry, current, _ = created 16 "PURE-IDENTITY"

    let corrupted =
        { entry with
            PayloadCommitment = Array.zeroCreate 32
        }

    Expect.equal
        (decide original (locked worker (Some current) (Some corrupted)))
        (Decision.Fail CoreFault.OperationContentConflict)
        "Stored corruption discloses no receipt"

    let _, _, readable, _ = created 17 "READABLE-CASE"
    let scoped = actor owner (id 1) [] [ readable.CaseId, [ Role.CaseHandler ] ]

    let different =
        { original with
            CaseReference = (CaseReference.format readable.Snapshot.Reference)
            ExpectedRevision = 1L
            Act = UnvalidatedCaseAct.Close
        }

    Expect.equal
        (decide different (locked scoped (Some readable) (Some entry)))
        (Decision.Refuse Refusal.ResourceUnavailable)
        "An inaccessible existing operation does not leak through another readable case"

let private capacityAndFence () =
    let request, entry, current, receipt = created 18 "PURE-CAPACITY"

    let full =
        { locked worker (Some current) None with
            Head =
                { head with
                    Sequence = Revision.Maximum
                }
        }

    Expect.equal
        (decide { request with OperationId = id 19 } full)
        (Decision.Refuse Refusal.LedgerExhausted)
        "Fresh work stops before arithmetic"

    Expect.equal
        (decide
            request
            { full with
                Read =
                    { full.Read with
                        ExistingOperation = Some entry
                    }
            })
        (Decision.Replay receipt)
        "Exact replay remains possible at capacity"

    let fenced =
        { locked worker None None with
            IsFenced = true
        }

    Expect.equal
        (decide (opening 20 "PURE-FENCE") fenced)
        (Decision.Refuse Refusal.OperationResolved)
        "Fenced identity cannot append"

    let maxRevision =
        { current with
            Snapshot =
                { current.Snapshot with
                    Revision = Revision.Maximum
                }
        }

    Expect.equal
        (decide
            (actRequest
                21
                (CaseReference.format current.Snapshot.Reference)
                Revision.Maximum
                UnvalidatedCaseAct.Close)
            (locked worker (Some maxRevision) None))
        (Decision.Refuse Refusal.RevisionExhausted)
        "Revision exhaustion is checked without overflow"

let private rawOpening =
    """{"kind":"CASE","operationId":"80000000-0000-4000-8000-000000000010","caseReference":"UNIT-001","expectedRevision":0,"act":{"kind":"OPEN","values":{"incidentDate":"2026-08-01","incidentNotificationDate":"2026-08-03","incidentCountry":"LV","claimantName":"Synthetic Party","insurerName":"Synthetic Insurer","claimedAmount":"1000.00","claimedCurrency":"EUR"}}}"""

let private respelledOpening =
    """{"kind":"CASE","act":{"values":{"claimedCurrency":"EUR","claimedAmount":"1000.00","insurerName":"Synthetic Insurer","claimantName":"Synthetic Party","incidentCountry":"LV","incidentNotificationDate":"2026-08-03","incidentDate":"2026-08-01"},"kind":"OPEN"},"expectedRevision":0,"caseReference":"UNIT-001","operationId":"80000000-0000-4000-8000-000000000010"}"""

let private decodeWire (text: string) =
    Codec.decode
        Limits.MaximumRequestBytes
        Api.operationExecute.Input
        (Text.Encoding.UTF8.GetBytes text)
    |> Result.map (function
        | OperationInput.Case value -> value
        | _ -> failtest "Expected a case execution request")
    |> Result.defaultWith (fun _ -> failtest "Synthetic raw request must decode")

let private wireSpelling () =
    let original = decodeWire rawOpening
    let view = locked worker None None
    let draft, projection, receipt = decide original view |> appended

    let current =
        match projection with
        | CaseProjection.Insert(id, snapshot) -> { CaseId = id; Snapshot = snapshot }
        | _ -> failtest "Insert"

    let replayView = locked worker (Some current) (Some(LedgerEntries.stored draft))

    let later =
        { replayView with
            Read =
                { replayView.Read with
                    ObservedAt = instant.AddMinutes 1.
                }
        }

    Expect.equal
        (decide (decodeWire respelledOpening) later)
        (Decision.Replay receipt)
        "Raw JSON re-spelling retains original receipt, executor and instant"

    Expect.equal
        (decide (decodeWire (respelledOpening.Replace("1000.00", "1000.0"))) later)
        (Decision.Replay receipt)
        "Equivalent decimal value retains the original receipt"

    Expect.equal
        (decide (decodeWire (respelledOpening.Replace("1000.00", "1000.01"))) later)
        (Decision.Refuse Refusal.IdempotencyConflict)
        "A different amount still conflicts"

[<Tests>]
let tests =
    testList
        "pure case decisions"
        [
            testCase "raw JSON spelling preserves the accepted receipt" wireSpelling
            testCase "canonical replay preserves attribution and original identity" replay
            testCase "current resource authority precedes replay and creation" authorization
            testCase "review business date and revision use the supplied view" reviewAndTime
            testCase
                "corruption faults and foreign identity privacy remain distinct"
                integrityAndPrivacy
            testCase "head revision and operation fences retain refusal precedence" capacityAndFence
        ]
