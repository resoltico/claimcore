module ClaimCore.IntegrationTests.TimelineQueryTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected a readable timeline: %A" other

let private execute owner input =
    task { let! reply = Service.execute owner input token in return accepted reply }

[<Tests>]
let tests =
    testCaseAsync
        "timeline rechecks disclosure, scopes cursors and resolves prior assignees"
        (run (fun _ fixture ->
            task {
                let! owner = caseWorker fixture
                let target = CaseSelector.Reference "TIMELINE-API"

                let request =
                    {
                        CaseTimelineRequest.Target = target
                        Filter = None
                        AfterCursor = None
                        Limit = 1
                    }

                let facts =
                    Inputs.registrationWith
                        [
                            BusinessField.ClaimedAmount, "1000"
                            BusinessField.ClaimedCurrency, "EUR"
                        ]
                        registration

                let! _ =
                    execute
                        owner
                        (Inputs.caseOperation (
                            StoreFixtures.request
                                99200
                                "TIMELINE-API"
                                0L
                                (AuthoredCaseAct.Open facts)
                        ))

                for number, act in
                    [
                        99201,
                        AuthoredReminderAct.Create
                            {
                                Action = "First action"
                                Due = "2027-12-30"
                            }
                        99202,
                        AuthoredReminderAct.CompleteAndPlanNext(
                            id 99201,
                            1L,
                            {
                                Action = "Next action"
                                Due = "2027-12-31"
                            }
                        )
                        99203,
                        AuthoredReminderAct.Update(
                            id 99202,
                            1L,
                            {
                                Action = "Updated next action"
                                Due = "2027-12-31"
                            }
                        )
                    ] do
                    let! _ =
                        execute
                            owner
                            (OperationInput.Reminder
                                {
                                    OperationId = id number
                                    Target = target
                                    Act = act
                                })

                    ()

                let! first = Service.timeline owner request token
                let first = read first

                let cursor =
                    first.NextCursor
                    |> Option.defaultWith (fun () ->
                        failtest "The mixed history crosses a page boundary")

                let mutable collected = first.Items
                let mutable next = first.NextCursor

                while next.IsSome do
                    let! page = Service.timeline owner { request with AfterCursor = next } token
                    let page = read page
                    Expect.equal page.Items.Length 1 "Every page obeys the admitted limit"
                    collected <- collected @ page.Items
                    next <- page.NextCursor

                Expect.equal
                    (collected |> List.map (fun entry -> OperationId.value entry.OperationId))
                    ([ 99203; 99202; 99201; 99200 ] |> List.map id)
                    "Timeline order follows ledger sequence across independent case and reminder revisions"

                Expect.isTrue
                    (collected
                     |> List.pairwise
                     |> List.forall (fun (newer, older) ->
                         newer.Position.Sequence > older.Position.Sequence))
                    "The shared sequence is strictly descending"

                Expect.isTrue
                    (collected
                     |> List.forall (fun entry ->
                         match entry.Changes with
                         | TimelineChanges.Disclosed _ -> true
                         | _ -> false))
                    "Full-history access receives core differences"

                let! changedFilter =
                    Service.timeline
                        owner
                        { request with
                            Filter = Some TimelineFilter.Facts
                            AfterCursor = Some cursor
                        }
                        token

                Expect.equal
                    changedFilter
                    (Outcome.Rejected Refusal.InvalidCursor)
                    "A cursor cannot move to another timeline filter"

                let! missing =
                    Service.timeline
                        owner
                        { request with
                            Target = CaseSelector.Reference "NOT-THERE"
                            AfterCursor = Some cursor
                        }
                        token

                Expect.equal
                    missing
                    (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                    "Target disclosure precedes cursor validation"

                let principal =
                    PrincipalKey.human "https://issuer.example/" "timeline-reader"
                    |> Result.defaultWith (fun _ -> failtest "Principal fixture")

                let! _ =
                    execute
                        owner
                        (OperationInput.Authority(
                            authorityRequest
                                (id 99210)
                                principal
                                (AuthorityAct.Register Inputs.actorProfile)
                        ))

                let grantValue =
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.CaseReference "TIMELINE-API"
                    }

                let! granted = grant owner (id 99211) principal grantValue true
                acceptedAuthority granted
                let reader = { owner with Principal = principal }
                let! withheld = Service.timeline reader { request with Limit = 50 } token

                Expect.isTrue
                    ((read withheld).Items
                     |> List.forall (fun entry -> entry.Changes = TimelineChanges.Withheld))
                    "Summary-history access never receives earlier payload values"

                let! foreign =
                    Service.timeline
                        reader
                        { request with
                            AfterCursor = Some cursor
                        }
                        token

                Expect.equal
                    foreign
                    (Outcome.Rejected Refusal.InvalidCursor)
                    "A cursor is bound to its requesting actor"

                let! eligible =
                    grant
                        owner
                        (id 99212)
                        principal
                        { grantValue with
                            Role = Role.CaseHandler
                        }
                        true

                acceptedAuthority eligible

                for number, revision, responsibility in
                    [
                        99213, 0L, WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 99210)))
                        99214, 1L, WorkValue.Clear
                    ] do
                    let! _ =
                        execute
                            owner
                            (OperationInput.Work
                                {
                                    OperationId = id number
                                    Target = target
                                    ExpectedRevision = revision
                                    Act =
                                        WorkAct.UpdateWork
                                            {
                                                Responsible = responsibility
                                                Summary = WorkValue.Keep
                                            }
                                })

                    ()

                let! cleared = Service.timeline owner request token

                match (read cleared).Items.Head.Changes with
                | TimelineChanges.Disclosed {
                                                Items = [ TimelineChange.Responsibility difference ]
                                            } ->
                    Expect.equal
                        (difference.Before |> Option.map (fun actor -> ActorId.value actor.ActorId))
                        (Some(id 99210))
                        "A page resolves an assignee present only in its predecessor snapshot"

                    Expect.isNone difference.After "The latest work value is actually cleared"
                | other -> failtestf "Unexpected responsibility difference: %A" other

                let! disabled =
                    execute
                        owner
                        (OperationInput.Authority(
                            authorityRequest (id 99215) principal AuthorityAct.Disable
                        ))

                disabled |> ignore
                let! denied = Service.timeline reader request token

                Expect.equal
                    denied
                    (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                    "Disabling the actor revokes timeline access"

                let update: RegistrationUpdateInput =
                    {
                        IncidentDate = None
                        NotificationDate = None
                        Country = None
                        Claimant = None
                        Insurer = None
                        Claimed = Some { Amount = "2000"; Currency = "EUR" }
                        IncidentDescription = WorkValue.Keep
                    }

                let! _ =
                    execute
                        owner
                        (Inputs.caseOperation (
                            StoreFixtures.request
                                99216
                                "TIMELINE-API"
                                1L
                                (AuthoredCaseAct.AmendRegistration update)
                        ))

                let! money = Service.timeline owner request token

                match (read money).Items.Head.Changes with
                | TimelineChanges.Disclosed {
                                                Items = [ TimelineChange.CaseMoney difference ]
                                            } ->
                    Expect.equal
                        (difference.Change.Before |> Option.map Money.currency)
                        (Some "EUR")
                        "The old amount retains its unchanged currency"

                    Expect.equal
                        (difference.Change.After |> Option.map Money.currency)
                        (Some "EUR")
                        "The new amount retains its unchanged currency"

                    Expect.equal
                        (difference.Change.Before |> Option.map Money.format)
                        (Some "1000")
                        "Amounts stay exact"

                    Expect.equal
                        (difference.Change.After |> Option.map Money.format)
                        (Some "2000")
                        "Amounts stay exact"
                | other -> failtestf "Unexpected money difference: %A" other

                let! steward =
                    grant
                        owner
                        (id 99217)
                        fixture.Owner
                        {
                            Role = Role.DataSteward
                            Scope = GrantTarget.Installation
                        }
                        true

                acceptedAuthority steward

                let! revoked =
                    grant
                        owner
                        (id 99218)
                        fixture.Owner
                        {
                            Role = Role.CaseHandler
                            Scope = GrantTarget.Installation
                        }
                        false

                acceptedAuthority revoked

                let! downgraded =
                    Service.timeline
                        owner
                        { request with
                            AfterCursor = Some cursor
                        }
                        token

                Expect.isTrue
                    ((read downgraded).Items
                     |> List.forall (fun entry -> entry.Changes = TimelineChanges.Withheld))
                    "A retained cursor cannot retain revoked full-history disclosure"

                let! _ =
                    execute
                        owner
                        (Inputs.caseOperation (
                            StoreFixtures.request
                                99219
                                "TIMELINE-API"
                                2L
                                (AuthoredCaseAct.Bare BareAct.Erase)
                        ))

                let! erased = Service.timeline owner { request with Limit = 50 } token

                Expect.equal
                    (read erased).Items.Length
                    8
                    "Erasure retains every accepted metadata entry"

                Expect.isTrue
                    ((read erased).Items
                     |> List.forall (fun entry -> entry.Changes = TimelineChanges.Erased))
                    "Erasure takes precedence over full or summary disclosure"
            }))
