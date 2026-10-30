module ClaimCore.TestSupport.WorklistTriageContract

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None
let private maximum = "999999999999999999.9999"

let private execute owner number reference revision act =
    task {
        let! reply =
            Service.execute owner (OperationInput.Case(request number reference revision act)) token

        match reply.Outcome with
        | ExecuteResult.Accepted _ -> accepted reply |> ignore
        | _ -> failtestf "Triage operation %d on %s failed: %A" number reference reply.Outcome
    }

type private CaseSeed =
    {
        Reference: string
        Notification: string
        Amount: string
        Currency: string
    }

let private openCase owner number (seed: CaseSeed) =
    let values =
        Inputs.registrationWith
            [
                BusinessField.IncidentNotificationDate, seed.Notification
                BusinessField.ClaimedAmount, seed.Amount
                BusinessField.ClaimedCurrency, seed.Currency
            ]
            Inputs.registration

    execute owner number seed.Reference 0L (AuthoredCaseAct.Open values)

let private setup fixture =
    task {
        let! owner = caseWorker fixture

        let! steward =
            grant
                owner
                (id 10)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority steward

        do!
            openCase
                owner
                20
                {
                    Reference = "TRIAGE-A"
                    Notification = "2026-08-03"
                    Amount = maximum
                    Currency = "EUR"
                }

        do!
            openCase
                owner
                21
                {
                    Reference = "TRIAGE-B"
                    Notification = "2026-08-02"
                    Amount = maximum
                    Currency = "EUR"
                }

        do!
            openCase
                owner
                22
                {
                    Reference = "TRIAGE-C"
                    Notification = "2026-08-02"
                    Amount = "0.0001"
                    Currency = "USD"
                }

        do!
            openCase
                owner
                23
                {
                    Reference = "TRIAGE-D"
                    Notification = "2026-08-01"
                    Amount = "42.4200"
                    Currency = "JPY"
                }

        let decision =
            Inputs.decisionWith [ BusinessField.PayableAmount, maximum ] Inputs.decision

        do! execute owner 24 "TRIAGE-A" 1L (AuthoredCaseAct.Decide decision)
        do! execute owner 25 "TRIAGE-A" 2L (AuthoredCaseAct.Bare BareAct.Close)
        do! execute owner 26 "TRIAGE-B" 1L (AuthoredCaseAct.Decide decision)
        do! execute owner 27 "TRIAGE-B" 2L (AuthoredCaseAct.Void "Synthetic triage review")
        do! execute owner 28 "TRIAGE-D" 1L (AuthoredCaseAct.Bare BareAct.Erase)

        for number, reference in [ 30, "TRIAGE-B"; 31, "TRIAGE-C" ] do
            let! granted =
                grant
                    owner
                    (id number)
                    fixture.Owner
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.CaseReference reference
                    }
                    true

            acceptedAuthority granted

        return owner
    }

let private summary actor filter =
    task {
        let! result = Service.caseSummary actor filter token

        match result with
        | Outcome.Succeeded result -> return result
        | _ -> return failtestf "Expected filtered summary: %A" result
    }

let private counts value entries =
    entries |> List.find (fun entry -> entry.Value = value) |> _.Count

let private totals (values: CurrencyTotal list) =
    values
    |> List.map (fun value -> Currency.format value.Currency, DecimalTotal.format value.Amount)

let internal exactTotals fixture =
    task {
        let! owner = setup fixture
        let! all = summary owner CaseListFilter.empty<string>
        Expect.equal all.Counts.Total 4L "Every visible lifecycle is counted"
        Expect.equal (counts Lifecycle.Active all.Counts.Lifecycles) 2L "Active within filter"
        Expect.equal (counts Lifecycle.Voided all.Counts.Lifecycles) 1L "Voided within filter"
        Expect.equal (counts Lifecycle.Erased all.Counts.Lifecycles) 1L "Erased within filter"
        Expect.equal (counts CaseStatus.Closed all.Counts.Statuses) 1L "Erased rows have no status"

        Expect.equal
            (counts CaseProgress.Decided all.Counts.Progress)
            2L
            "Both retained decisions count"

        Expect.equal
            (totals all.Claimed)
            [ "EUR", "1999999999999999999.9998"; "USD", "0.0001" ]
            "Exact per-currency sums retain voided amounts and exclude erased amounts"

        Expect.equal
            (totals all.Payable)
            [ "EUR", "1999999999999999999.9998" ]
            "No decimal overflow or currency conversion"

        let! active =
            summary
                owner
                { CaseListFilter.empty<string> with
                    Lifecycles = [ Lifecycle.Active ]
                }

        Expect.equal active.Counts.Total 2L "Counts apply the current filter"

        Expect.equal
            (totals active.Claimed)
            [ "EUR", maximum; "USD", "0.0001" ]
            "Facets are not self-excluding"

        let mine =
            { CaseListFilter.empty<string> with
                Assignment = Some CaseAssignment.AssignedToMe
            }

        let! assigned = summary owner mine

        Expect.equal
            assigned.Counts.Total
            2L
            "Installation visibility does not replace scoped assignment"

        Expect.equal
            (totals assigned.Payable)
            [ "EUR", maximum ]
            "Assigned scope filters payable totals"

        let! context = Service.context owner () token

        match context with
        | Outcome.Succeeded value -> Expect.isTrue value.HasAssignedCases "My cases can be offered"
        | _ -> failtest "Expected context"

        let! case = Service.get owner { CaseReference = "TRIAGE-B" } token

        match case with
        | Outcome.Succeeded value ->
            Expect.isTrue value.AssignedToYou "Case detail derives its assignment"
        | _ -> failtest "Expected assigned case"
    }

let private list actor filter cursor =
    task {
        let! result =
            Service.list
                actor
                {
                    Page = { Limit = 1; AfterCursor = cursor }
                    Filter = filter
                }
                token

        match result with
        | Outcome.Succeeded value -> return value
        | _ -> return failtestf "Expected ordered worklist: %A" result
    }

let internal oldestPaging fixture =
    task {
        let! owner = setup fixture

        let filter =
            { CaseListFilter.empty<string> with
                Order = CaseListOrder.Notified
            }

        let! first = list owner filter None
        let! second = list owner filter first.NextCursor
        let! third = list owner filter second.NextCursor
        let! fourth = list owner filter third.NextCursor
        let rows = [ first; second; third; fourth ] |> List.collect _.Items

        Expect.equal
            (rows |> List.map (fun row -> CaseReference.format row.Reference))
            [ "TRIAGE-B"; "TRIAGE-C"; "TRIAGE-A"; "TRIAGE-D" ]
            "Oldest dates first, reference ties, erased last"

        Expect.isNone fourth.NextCursor "The erased tail completes the keyset"

        let! changed =
            Service.list
                owner
                {
                    Page =
                        {
                            Limit = 1
                            AfterCursor = first.NextCursor
                        }
                    Filter =
                        { filter with
                            Assignment = Some CaseAssignment.AssignedToMe
                        }
                }
                token

        Expect.equal changed (Outcome.Rejected Refusal.InvalidCursor) "Assignment binds cursors"
    }

let internal assignmentExcludesForbiddenRoles fixture =
    task {
        let! owner = setup fixture

        let service =
            PrincipalKey.service "https://issuer.example/" "triage-service"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 100) service AuthorityAct.Register))
                token

        let actorId =
            match (accepted registered).Subject with
            | ReceiptSubject.Authority value -> value.TargetActorId
            | _ -> failtest "Expected authority receipt"

        for number, scope in
            [
                101, GrantTarget.Installation
                102, GrantTarget.CaseReference "TRIAGE-B"
                103, GrantTarget.CaseReference "TRIAGE-C"
            ] do
            let! granted =
                grant
                    owner
                    (id number)
                    service
                    {
                        Role = Role.CaseReader
                        Scope = scope
                    }
                    true

            acceptedAuthority granted

        let viewer = core fixture service

        let mine =
            { CaseListFilter.empty<string> with
                Assignment = Some CaseAssignment.AssignedToMe
            }

        let! first = summary viewer mine
        Expect.equal first.Counts.Total 2L "A service's valid scoped grants define assignment"
        // An inconsistent projection is injected through the real store port, without changing
        // any SQL constraint or authorization rule. The visibility safeguards must exclude it.
        let caseId =
            ClaimCore.RecordFormat.CaseReferenceId.encode fixture.InstallationId "TRIAGE-B"

        let! altered =
            fixture.Store.Write
                {
                    Work =
                        fun tx ->
                            task {
                                do!
                                    tx.ProjectAuthority(
                                        AuthorityProjection.AddGrant(
                                            actorId,
                                            Role.DataSteward,
                                            Some caseId
                                        )
                                    )

                                return Verdict.Proceed()
                            }
                    CancellationToken = token
                }

        Expect.isOk altered "Owned fixture projection corruption committed"
        let! filtered = summary viewer mine

        Expect.equal
            filtered.Counts.Total
            1L
            "A forbidden role excludes that case despite another valid grant"

        Expect.equal
            (totals filtered.Claimed)
            [ "USD", "0.0001" ]
            "Only the valid assigned case contributes"

        let! visible = summary viewer CaseListFilter.empty<string>

        Expect.equal
            visible.Counts.Total
            3L
            "Installation visibility retains the same independent safeguard"

        let! denied = Service.get viewer { CaseReference = "TRIAGE-B" } token

        Expect.equal
            denied
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "An assignment marker cannot bypass the case read boundary"

        let! revoked =
            grant
                owner
                (id 104)
                service
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseReference "TRIAGE-C"
                }
                false

        acceptedAuthority revoked
        let! empty = summary viewer mine
        Expect.equal empty.Counts.Total 0L "Invalid grants never become fallback assignments"
    }
