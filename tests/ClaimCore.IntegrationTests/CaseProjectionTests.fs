module ClaimCore.IntegrationTests.CaseProjectionTests

open ClaimCore.Contract

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
        State =
            CaseState.Active(
                {
                    CaseReference = reference
                    Status = CaseStatus.Opened
                    Registration =
                        {
                            IncidentDate = "2026-08-01"
                            IncidentNotificationDate = "2026-08-03"
                            IncidentCountry = "LV"
                            ClaimantName = "Synthetic Party"
                            InsurerName = "Synthetic Insurer"
                            ClaimedAmount = "1000.00"
                            ClaimedCurrency = "EUR"
                        }
                    Decision = None
                    Payment = None
                }
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
        State = CaseState.Active()
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
        match a.Snapshot.State with
        | (CaseState.Active(fields) | CaseState.Voided(fields)) -> fields
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
                        State = CaseState.Erased
                    }
            }
            { a with
                Snapshot =
                    { a.Snapshot with
                        State = CaseState.Voided(fields)
                    }
            }
            { a with
                Snapshot =
                    { a.Snapshot with
                        State =
                            CaseState.Active(
                                { fields with
                                    Registration =
                                        { fields.Registration with
                                            ClaimedAmount = "2000.00"
                                        }
                                }
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
            State = CaseState.Erased
        }

    let row = stored erased

    let accepted =
        { history live with
            Revision = 2L
            Snapshot = None
            State = CaseState.Erased
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
            { accepted with
                State = CaseState.Active()
            }
            { accepted with
                MissingPayloadAt = None
            }
        ] do
        Expect.equal (check changed row) mismatch "Erased evidence remains required"

let private projectionFields: CaseFields =
    {
        CaseReference = "synthetic-layout"
        Status = CaseStatus.Closed
        Registration =
            {
                IncidentDate = "2026-08-01"
                IncidentNotificationDate = "2026-08-03"
                IncidentCountry = "LV"
                ClaimantName = "Synthetic claimant"
                InsurerName = "Synthetic insurer"
                ClaimedAmount = "1234.5678"
                ClaimedCurrency = "EUR"
            }
        Decision =
            Some
                {
                    PaymentDecisionDate = "2026-08-04"
                    PayableAmount = "987.6543"
                    PayableCurrency = "USD"
                }
        Payment = Some { PaymentDate = "2026-08-05" }
    }


// Independent SQL row, with distinct values for every interchangeable date/text/amount.
let private projectionRow id : objnull array =
    [|
        box id
        box "synthetic-layout"
        box 4L
        box "VOIDED"
        box "2026-08-01 AD"
        box "2026-08-03 AD"
        box "LV"
        box "Synthetic claimant"
        box "Synthetic insurer"
        box "1234.5678"
        box "EUR"
        box "2026-08-04 AD"
        box "987.6543"
        box "USD"
        box "2026-08-05 AD"
        box "CLOSED"
    |]


let private projectionBindings () =
    let fields = projectionFields

    let snapshot =
        {
            Reference = fields.CaseReference
            Revision = 4L
            State = CaseState.Voided(fields)
        }

    let id = CaseReferenceId.encode installation fields.CaseReference
    let row = projectionRow id

    Expect.equal
        (Projections.caseRow (Array.map nonNull row))
        { CaseId = id; Snapshot = snapshot }
        "Every decoded field has its own value"

    let expected = Array.copy row

    for index in [ 4; 5; 11; 14 ] do
        expected[index] <-
            box (
                DateOnly.ParseExact(
                    unbox<string> row[index],
                    "yyyy-MM-dd 'AD'",
                    Globalization.CultureInfo.InvariantCulture
                )
            )

    for index in [ 9; 12 ] do
        expected[index] <-
            box (
                Decimal.Parse(unbox<string> row[index], Globalization.CultureInfo.InvariantCulture)
            )

    Expect.equal
        (CaseProjectionLayout.parameters id snapshot)
        expected
        "Every bound parameter matches its SQL column"

    let swapped = Array.copy row
    swapped[7] <- row[8]
    swapped[8] <- row[7]

    Expect.notEqual
        (Projections.caseRow (Array.map nonNull swapped)).Snapshot
        snapshot
        "Swapped text bindings are observable"

[<Tests>]
let tests =
    testList
        "case projection collection"
        [
            testCase "binds and decodes every field in the SQL projection" projectionBindings
            testCase "requires complete unique case membership" collection
            testCase "preserves live identity revision state and snapshot checks" liveState
            testCase "preserves erased identity revision and state checks" erasedState
        ]
