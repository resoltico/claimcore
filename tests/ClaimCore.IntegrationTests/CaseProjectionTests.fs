module ClaimCore.IntegrationTests.CaseProjectionTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat

let private installation = Guid.Parse "80000000-0000-4000-8000-000000000001"

let private snapshot (reference: string) : CaseSnapshot =
    {
        Reference = reference
        Revision = 1L
        View =
            CaseView.Live(
                {
                    IncidentDate = "2026-08-01"
                    IncidentNotificationDate = "2026-08-03"
                    IncidentCountry = "LV"
                    ClaimantName = "Synthetic Party"
                    InsurerName = "Synthetic Insurer"
                    ClaimedAmount = "1000.00"
                    ClaimedCurrency = "EUR"
                    CaseReference = reference
                    PaymentDecisionDate = None
                    PayableAmount = None
                    PayableCurrency = None
                    PaymentDate = None
                    Status = CaseStatus.Opened
                },
                Disposition.Active
            )
    }

let private stored (snapshot: CaseSnapshot) : StoredCase =
    {
        CaseId = CaseReferenceId.encode installation snapshot.Reference
        Snapshot = snapshot
    }

let private history (snapshot: CaseSnapshot) : CaseHistory =
    {
        Revision = snapshot.Revision
        Snapshot = Some snapshot
        Reference = Some snapshot.Reference
        Disposition = Disposition.Active
        Erased = false
        MissingPayloadAt = None
        PresentPayloadAt = Some 1L
        ErasedAt = None
    }

let private mismatch =
    Error
        {
            Sequence = None
            Finding = VerificationFinding.CaseProjection
        }

let private collection () =
    let a, b = stored (snapshot "synthetic-a"), stored (snapshot "synthetic-b")

    let histories =
        Map.ofList [ a.CaseId, history a.Snapshot; b.CaseId, history b.Snapshot ]

    Expect.equal (CaseFold.projection installation Map.empty []) (Ok()) "Empty coverage"

    for rows in [ [ a; b ]; [ b; a ] ] do
        Expect.equal
            (CaseFold.projection installation histories rows)
            (Ok())
            "Exact unordered coverage"

    for rows in [ [ a; a ]; [ a ]; [ a; b; a ]; [ a; stored (snapshot "unknown") ] ] do
        Expect.equal
            (CaseFold.projection installation histories rows)
            mismatch
            "Exact collection finding"

let private liveState () =
    let a = stored (snapshot "synthetic-a")
    let histories = Map.ofList [ a.CaseId, history a.Snapshot ]

    let fields =
        match a.Snapshot.View with
        | CaseView.Live(fields, _) -> fields
        | _ -> failtest "Live fixture"

    for changed in
        [
            { a with CaseId = Guid.Empty }
            { a with
                Snapshot =
                    { a.Snapshot with
                        Reference = "another"
                    }
            }
            { a with
                Snapshot = { a.Snapshot with Revision = 2L }
            }
            { a with
                Snapshot =
                    { a.Snapshot with
                        View = CaseView.Erased
                    }
            }
            { a with
                Snapshot =
                    { a.Snapshot with
                        View = CaseView.Live(fields, Disposition.Voided)
                    }
            }
            { a with
                Snapshot =
                    { a.Snapshot with
                        View =
                            CaseView.Live(
                                { fields with
                                    ClaimedAmount = "2000.00"
                                },
                                Disposition.Active
                            )
                    }
            }
        ] do
        Expect.equal
            (CaseFold.projection installation histories [ changed ])
            mismatch
            "Live mismatch"

let private erasedState () =
    let live = snapshot "synthetic-erased"

    let erased =
        { live with
            Revision = 2L
            View = CaseView.Erased
        }

    let row = stored erased

    let accepted =
        { history live with
            Revision = 2L
            Snapshot = None
            Erased = true
            MissingPayloadAt = Some 1L
            PresentPayloadAt = None
            ErasedAt = Some 2L
        }

    let check (history: CaseHistory) (row: StoredCase) =
        CaseFold.projection installation (Map.ofList [ row.CaseId, history ]) [ row ]

    Expect.equal (check accepted row) (Ok()) "Erased identity and revision remain covered"

    for changed in
        [
            { row with CaseId = Guid.Empty }
            { row with
                Snapshot = { erased with Reference = "another" }
            }
            { row with
                Snapshot = { erased with Revision = 1L }
            }
            { row with Snapshot = live }
        ] do
        Expect.equal (check accepted changed) mismatch "Erased identity revision and state refuse"

    for changed in
        [
            { accepted with Erased = false }
            { accepted with
                MissingPayloadAt = None
            }
        ] do
        Expect.equal (check changed row) mismatch "Erased evidence remains required"

[<Tests>]
let tests =
    testList
        "case projection collection"
        [
            testCase "requires complete unique case membership" collection
            testCase "preserves live identity revision disposition and snapshot checks" liveState
            testCase "preserves erased identity revision and state checks" erasedState
        ]
