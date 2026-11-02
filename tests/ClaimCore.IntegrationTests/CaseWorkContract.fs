module ClaimCore.TestSupport.CaseWorkContract

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected read success: %A" other

let private execute core operation reference revision act =
    Service.execute core (Inputs.caseOperation (request operation reference revision act)) token

let private get core caseId =
    Service.get core { Target = CaseSelector.Id caseId } token

let private candidates core caseId after limit =
    Service.assignees
        core
        {
            Target = CaseSelector.Id caseId
            AfterCursor = after
            Limit = limit
        }
        token

let private list core filter =
    Service.list
        core
        {
            Page = { AfterCursor = None; Limit = 50 }
            Filter = filter
        }
        token

let internal assignmentAndDisclosure fixture =
    task {
        let! owner = caseWorker fixture

        let person =
            PrincipalKey.human "https://issuer.example/" "case-work-assignee"
            |> Result.defaultWith (string >> failtest)

        let reader =
            PrincipalKey.human "https://issuer.example/" "scoped-reader"
            |> Result.defaultWith (string >> failtest)

        let automation =
            PrincipalKey.service "https://issuer.example/" "service-handler"
            |> Result.defaultWith (string >> failtest)

        for operation, principal in [ 10, person; 11, reader; 12, automation ] do
            let! registered =
                Service.execute
                    owner
                    (OperationInput.Authority(
                        authorityRequest (id operation) principal AuthorityAct.Register
                    ))
                    token

            acceptedAuthority registered

        let! opened = execute owner 20 "WORK-A" 0L (CaseAct.Open registration)
        let caseId = accepted opened |> caseReceipt |> _.CaseId
        let! other = execute owner 21 "WORK-B" 0L (CaseAct.Open registration)
        let otherId = accepted other |> caseReceipt |> _.CaseId

        for operation, principal, role, scope in
            [
                22, person, Role.CaseHandler, GrantTarget.CaseId caseId
                23, reader, Role.CaseReader, GrantTarget.CaseId caseId
                24, automation, Role.CaseHandler, GrantTarget.Installation
            ] do
            let! changed =
                grant owner (id operation) principal { Role = role; Scope = scope } true

            acceptedAuthority changed

        let! first = candidates owner caseId None 1
        let first = read first

        Expect.equal
            (first.Items |> List.map _.ActorId)
            [ CaseFixtures.actorId (id 1) ]
            "First bounded eligible human"

        Expect.isSome first.NextCursor "The second eligible account remains discoverable"
        let! second = candidates owner caseId first.NextCursor 1
        let second = read second

        Expect.equal
            second.Items
            [
                {
                    ActorId = CaseFixtures.actorId (id 10)
                    Subject = "case-work-assignee"
                }
            ]
            "Only purpose-limited account identity is disclosed"

        Expect.isNone second.NextCursor "No reader, service or unrelated account is included"
        let! wrongScope = candidates owner otherId first.NextCursor 1

        Expect.equal
            wrongScope
            (Outcome.Rejected Refusal.InvalidCursor)
            "Candidate cursors bind the actual case"

        for principal in [ reader; automation ] do
            let! denied = candidates (core fixture principal) caseId None 10

            Expect.equal
                denied
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Reading a case cannot reveal an assignee directory"

        let! readerCase = get (core fixture reader) caseId
        Expect.isFalse (read readerCase).Mine "A scoped read grant is not responsibility"
        let! context = Service.context owner () token
        let today = (read context).Runtime.EffectiveBusinessDate
        let tomorrow = CalendarDate.ofDate (today.AddDays 1) |> CalendarDate.format

        let update: AuthoredCaseAct =
            CaseAct.UpdateWork
                {
                    Responsible = WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 10)))
                    FollowUp = WorkValue.Set tomorrow
                }

        let! assigned = execute owner 30 "WORK-A" 1L update
        let assigned = accepted assigned

        Expect.equal
            (disclosedFields assigned).Work.Responsible
            (Some(CaseFixtures.actorId (id 10)))
            "The erasable fact is an ActorId"

        let! current = get owner caseId
        let current = read current
        Expect.equal current.Work.Due CaseDue.Upcoming "A future planned date is valid work"

        Expect.equal
            current.Work.Responsible
            (Some
                {
                    Account =
                        {
                            ActorId = CaseFixtures.actorId (id 10)
                            Subject = "case-work-assignee"
                        }
                    Available = true
                })
            "Relevant current subject and eligibility"

        let! personCase = get (core fixture person) caseId
        Expect.isTrue (read personCase).Mine "Assignment plus current read authority is Mine"

        let! readerCases =
            list
                (core fixture reader)
                { CaseListFilter.empty with
                    Assignment = Some CaseAssignment.Mine
                }

        Expect.isEmpty (read readerCases).Items "A visible grant-only case stays outside Mine"

        let pending =
            Inputs.caseOperation (
                request
                    31
                    "WORK-A"
                    2L
                    (CaseAct.UpdateWork
                        {
                            Responsible =
                                WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 10)))
                            FollowUp =
                                WorkValue.Set(
                                    CalendarDate.ofDate (today.AddDays 2) |> CalendarDate.format
                                )
                        })
            )

        let! preview = Service.preview owner pending token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | _ -> failtest "Preview positive control"

        let! revoked =
            grant
                owner
                (id 32)
                person
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.CaseId caseId
                }
                false

        acceptedAuthority revoked
        let! refused = Service.execute owner pending token

        Expect.equal
            refused.Outcome
            (ExecuteResult.Rejected(Refusal.Case CaseRefusal.AssigneeUnavailable))
            "Commit rechecks assignment after preview"

        let! retained = get owner caseId
        let retained = read retained

        Expect.equal
            (Revision.value retained.Snapshot.Revision)
            2L
            "Revocation and refusal create no case revision"

        Expect.equal
            (retained.Work.Responsible |> Option.map _.Available)
            (Some false)
            "Lost eligibility remains visible without silent reassignment"

        Expect.equal
            (CaseState.fields retained.Snapshot.State |> Option.get |> _.Work.Responsible)
            (Some(CaseFixtures.actorId (id 10)))
            "Assignment history stays factual"

        let! hidden = get (core fixture person) caseId

        Expect.equal
            hidden
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Assignment never preserves access after revocation"

        let! mine =
            list
                (core fixture person)
                { CaseListFilter.empty with
                    Assignment = Some CaseAssignment.Mine
                }

        Expect.equal
            mine
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Mine cannot invent visibility"

        let! disabled =
            Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 33) person AuthorityAct.Disable))
                token

        acceptedAuthority disabled
        let! afterDisable = get owner caseId

        Expect.equal
            (read afterDisable).Snapshot.Revision
            retained.Snapshot.Revision
            "Account disable is not a case update"

        let! serviceWork =
            execute
                (core fixture automation)
                34
                "WORK-A"
                2L
                (CaseAct.UpdateWork
                    {
                        Responsible = WorkValue.Clear
                        FollowUp = WorkValue.Keep
                    })

        Expect.equal
            serviceWork.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ActNotPermitted))
            "Service CASE_HANDLER has no work capability"

        for operation, target in [ 90, 11; 91, 12; 92, 999 ] do
            let! unavailable =
                execute
                    owner
                    operation
                    "WORK-A"
                    2L
                    (CaseAct.UpdateWork
                        {
                            Responsible =
                                WorkValue.Set(ActorId.format (CaseFixtures.actorId (id target)))
                            FollowUp = WorkValue.Keep
                        })

            Expect.equal
                unavailable.Outcome
                (ExecuteResult.Rejected(Refusal.Case CaseRefusal.AssigneeUnavailable))
                "Readers, services and unknown identities cannot be assigned"

        let! cleared =
            execute
                owner
                35
                "WORK-A"
                2L
                (CaseAct.UpdateWork
                    {
                        Responsible = WorkValue.Clear
                        FollowUp = WorkValue.Keep
                    })

        Expect.equal
            (disclosedFields (accepted cleared)).Work
            {
                Responsible = None
                FollowUp =
                    Some(CalendarDate.parse tomorrow |> Result.defaultWith (string >> failtest))
            }
            "Clearing responsibility preserves the independent follow-up"

        let! unchanged =
            execute
                owner
                36
                "WORK-A"
                3L
                (CaseAct.UpdateWork
                    {
                        Responsible = WorkValue.Clear
                        FollowUp = WorkValue.Keep
                    })

        Expect.equal
            unchanged.Outcome
            (ExecuteResult.Rejected(Refusal.Case CaseRefusal.WorkNoChanges))
            "Unchanged work cannot advance revision"
    }

let internal dueFiltersAndOrder fixture =
    task {
        let! owner = caseWorker fixture
        let! context = Service.context owner () token
        let context = read context
        let today = context.Runtime.EffectiveBusinessDate

        for n, reference, followUp, responsible in
            [
                50, "WORK-OVERDUE", Some(today.AddDays -1), false
                51, "WORK-TODAY", Some today, true
                52, "WORK-UPCOMING", Some(today.AddDays 1), false
                53, "WORK-UNSCHEDULED", None, false
            ] do
            let! opened = execute owner n reference 0L (CaseAct.Open registration)
            accepted opened |> ignore

            match followUp with
            | None -> ()
            | Some date ->
                let! updated =
                    execute
                        owner
                        (n + 10)
                        reference
                        1L
                        (CaseAct.UpdateWork
                            {
                                Responsible =
                                    (if responsible then
                                         WorkValue.Set(ActorId.format context.ActorId)
                                     else
                                         WorkValue.Keep)
                                FollowUp =
                                    WorkValue.Set(CalendarDate.ofDate date |> CalendarDate.format)
                            })

                accepted updated |> ignore

        for due, expected in
            [
                CaseDue.Overdue, "WORK-OVERDUE"
                CaseDue.Today, "WORK-TODAY"
                CaseDue.Upcoming, "WORK-UPCOMING"
                CaseDue.Unscheduled, "WORK-UNSCHEDULED"
            ] do
            let! result =
                list
                    owner
                    { CaseListFilter.empty with
                        Due = Some due
                    }

            let page = read result

            Expect.equal
                (page.Items |> List.map (_.Reference >> CaseReference.format))
                [ expected ]
                "Independent due membership"

            Expect.equal
                page.Basis.BusinessDate
                today
                "Due basis comes from the installation snapshot"

            Expect.equal
                page.Basis.TimeZoneId
                context.Runtime.TimeZoneId
                "The installation zone remains explicit"

            for row in page.Items do
                Expect.equal
                    (CaseState.fields row.State |> Option.get |> _.Work.Basis)
                    page.Basis
                    "Each row uses this query observation"

        let! mine =
            list
                owner
                { CaseListFilter.empty with
                    Assignment = Some CaseAssignment.Mine
                }

        Expect.equal
            ((read mine).Items |> List.map (_.Reference >> CaseReference.format))
            [ "WORK-TODAY" ]
            "Mine is factual responsibility"

        let! unassigned =
            list
                owner
                { CaseListFilter.empty with
                    Assignment = Some CaseAssignment.Unassigned
                }

        Expect.equal
            ((read unassigned).Items |> List.map (_.Reference >> CaseReference.format))
            [ "WORK-OVERDUE"; "WORK-UNSCHEDULED"; "WORK-UPCOMING" ]
            "No assignment is separate from no date"

        let mutable after = None
        let mutable references = []

        for _ in 1..4 do
            let! result =
                Service.list
                    owner
                    {
                        Page = { AfterCursor = after; Limit = 1 }
                        Filter =
                            { CaseListFilter.empty with
                                Order = CaseListOrder.FollowUp
                            }
                    }
                    token

            let page = read result
            Expect.equal page.Items.Length 1 "The real keyset returns one visible row"

            references <-
                references @ (page.Items |> List.map (_.Reference >> CaseReference.format))

            after <- page.NextCursor

        Expect.equal
            references
            [ "WORK-OVERDUE"; "WORK-TODAY"; "WORK-UPCOMING"; "WORK-UNSCHEDULED" ]
            "Date ordering with unscheduled last"

        Expect.isNone after "Complete traversal"
        let! summary = Service.caseSummary owner CaseListFilter.empty token
        let summary = read summary
        Expect.equal summary.Counts.Total 4L "Independent selected total"

        Expect.equal
            (summary.Counts.Assignment |> List.map (fun value -> value.Value, value.Count))
            [ CaseAssignment.Mine, 1L; CaseAssignment.Unassigned, 3L ]
            "Responsibility counts agree"

        Expect.equal
            (summary.Counts.Due |> List.map (fun value -> value.Value, value.Count))
            [
                CaseDue.Overdue, 1L
                CaseDue.Today, 1L
                CaseDue.Upcoming, 1L
                CaseDue.Unscheduled, 1L
                CaseDue.Inactive, 0L
            ]
            "All due counts are independently expected"

        let! closed = execute owner 80 "WORK-OVERDUE" 2L (CaseAct.Bare BareAct.Close)

        Expect.equal
            ((disclosedFields (accepted closed)).Work.FollowUp
             |> Option.map CalendarDate.format)
            (Some(CalendarDate.ofDate (today.AddDays -1) |> CalendarDate.format))
            "Close retains the planned fact"

        let! overdue =
            list
                owner
                { CaseListFilter.empty with
                    Due = Some CaseDue.Overdue
                }

        Expect.isEmpty (read overdue).Items "Closed cases are not actionable overdue work"
    }

let internal lifecycleAndErasure fixture =
    task {
        let! owner = caseWorker fixture

        let person =
            PrincipalKey.human "https://issuer.example/" "erasable-assignment"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 10) person AuthorityAct.Register))
                token

        acceptedAuthority registered

        let! stewardship =
            grant
                owner
                (id 11)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority stewardship
        let! opened = execute owner 20 "WORK-LIFECYCLE" 0L (CaseAct.Open registration)
        let caseId = accepted opened |> caseReceipt |> _.CaseId

        let! eligible =
            grant
                owner
                (id 21)
                person
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.CaseId caseId
                }
                true

        acceptedAuthority eligible

        let! assigned =
            execute
                owner
                22
                "WORK-LIFECYCLE"
                1L
                (CaseAct.UpdateWork
                    {
                        Responsible = WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 10)))
                        FollowUp = WorkValue.Set "2027-12-31"
                    })

        let work = (disclosedFields (accepted assigned)).Work

        for operation, revision, act, lifecycle in
            [
                23, 2L, CaseAct.Bare BareAct.Close, Lifecycle.Active
                24, 3L, CaseAct.Bare BareAct.Reopen, Lifecycle.Active
                25, 4L, CaseAct.Void "Synthetic lifecycle control", Lifecycle.Voided
                26, 5L, CaseAct.Reinstate "Synthetic lifecycle control", Lifecycle.Active
            ] do
            let! result = execute owner operation "WORK-LIFECYCLE" revision act
            let receipt = accepted result

            Expect.equal
                (disclosedFields receipt).Work
                work
                "Lifecycle changes retain exact work facts"

            let! current = get owner caseId
            let current = read current
            Expect.equal (CaseState.lifecycle current.Snapshot.State) lifecycle "Actual lifecycle"

            if operation = 23 || operation = 25 then
                Expect.equal current.Work.Due CaseDue.Inactive "Closed and voided work is inactive"

                let! denied =
                    execute
                        owner
                        (operation + 100)
                        "WORK-LIFECYCLE"
                        (revision + 1L)
                        (CaseAct.UpdateWork
                            {
                                Responsible = WorkValue.Clear
                                FollowUp = WorkValue.Keep
                            })

                Expect.equal
                    denied.Outcome
                    (ExecuteResult.Rejected(
                        Refusal.Case(
                            if operation = 23 then
                                CaseRefusal.CaseClosed
                            else
                                CaseRefusal.CaseVoided
                        )
                    ))
                    "Inactive work cannot mutate"

        let! erased = execute owner 27 "WORK-LIFECYCLE" 6L (CaseAct.Bare BareAct.Erase)

        match (accepted erased).Subject with
        | ReceiptSubject.Case { Snapshot = SnapshotDisclosure.Erased } -> ()
        | _ -> failtest "Erasure receipt control"

        let! current = get owner caseId
        let current = read current
        Expect.equal current.Snapshot.State CaseState.Erased "No work payload survives"
        Expect.equal current.Work.Responsible None "No account subject relation is reconstructed"
        Expect.equal current.Work.FollowUp None "No planned date survives"
        Expect.equal current.Work.Due CaseDue.Inactive "Erasure is not an actionable task"

        let! stored =
            fixture.Store.Read
                {
                    Work =
                        fun tx ->
                            task {
                                let! record = tx.FindCase caseId

                                let! history =
                                    tx.HistoryPage
                                        {
                                            CaseId = caseId
                                            AfterRevision = Revision.zero
                                            Limit = 20
                                        }

                                return record, history
                            }
                    CancellationToken = token
                }

        let record, history = stored |> Result.defaultWith (string >> failtest)
        let record = record |> Option.get

        Expect.equal
            record.Index.Responsible
            None
            "The assignment foreign-key relationship is removed"

        Expect.equal record.Index.FollowUp None "No work index survives"
        Expect.equal history.Page.Items.Length 7 "Every case revision was inspected"

        for entry in history.Page.Items do
            Expect.equal entry.Payload None "All historical assignment/date payloads are erased"

            Expect.equal
                entry.ActorId
                (CaseFixtures.actorId (id 1))
                "Retained executor metadata does not acquire the assigned actor"
    }

let internal partialUpdatesAndReaffirmation fixture =
    task {
        let! owner = caseWorker fixture
        let! opened = execute owner 40 "PARTIAL-FACTS" 0L (CaseAct.Open registration)
        let caseId = accepted opened |> caseReceipt |> _.CaseId

        let partial: RegistrationUpdateInput =
            {
                IncidentDate = None
                NotificationDate = None
                Country = None
                Claimant = Some "Only selected claimant"
                Insurer = None
                Claimed = None
            }

        let! edited = execute owner 41 "PARTIAL-FACTS" 1L (CaseAct.AmendRegistration partial)
        let fields = disclosedFields (accepted edited)

        Expect.equal
            (PartyName.format (Registration.claimant fields.Registration))
            "Only selected claimant"
            "One asserted fact"

        Expect.equal
            (PartyName.format (Registration.insurer fields.Registration))
            "Synthetic Insurer"
            "Omitted insurer retained"

        Expect.equal
            (Money.format (Registration.claimed fields.Registration))
            "1000"
            "Omitted money retains its complete pair"

        let! stale =
            execute
                owner
                42
                "PARTIAL-FACTS"
                1L
                (CaseAct.AmendRegistration
                    { partial with
                        Claimant = Some "Stale edit"
                    })

        Expect.equal
            stale.Outcome
            (ExecuteResult.Rejected(Refusal.RevisionConflict(CaseFixtures.revision 2L)))
            "No automatic partial rebase"

        let! decided = execute owner 43 "PARTIAL-FACTS" 2L (CaseAct.Decide Inputs.decision)
        accepted decided |> ignore

        let! paid =
            execute owner 44 "PARTIAL-FACTS" 3L (CaseAct.RecordPayment Inputs.payment)

        accepted paid |> ignore

        let change: AuthoredCorrection =
            {
                Registration = Amendment.Keep
                Decision =
                    Change.Update
                        {
                            Date = None
                            Payable = Some { Amount = "650.00"; Currency = "USD" }
                        }
                Payment = PaymentChange.Keep
            }

        let! acknowledgement = execute owner 45 "PARTIAL-FACTS" 4L (CaseAct.Correct change)

        Expect.equal
            acknowledgement.Outcome
            (ExecuteResult.Rejected(
                Refusal.invalidInput (
                    InputTarget.Group RecordGroup.Payment,
                    Violation.PaymentAcknowledgementRequired
                )
            ))
            "No silent payment reuse"

        let reaffirm =
            CaseAct.Correct
                { change with
                    Payment = PaymentChange.Reaffirm
                }

        let! revoked =
            grant
                owner
                (id 46)
                fixture.Owner
                {
                    Role = Role.PaymentRecorder
                    Scope = GrantTarget.Installation
                }
                false

        acceptedAuthority revoked
        let! denied = execute owner 47 "PARTIAL-FACTS" 4L reaffirm

        Expect.equal
            denied.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ActNotPermitted))
            "Reaffirmation requires the payment capability"

        let! granted =
            grant
                owner
                (id 48)
                fixture.Owner
                {
                    Role = Role.PaymentRecorder
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
        let! updated = execute owner 49 "PARTIAL-FACTS" 4L reaffirm
        let updated = disclosedFields (accepted updated)

        Expect.equal
            (updated.Payment |> Option.map (Payment.date >> CalendarDate.format))
            (Some "2026-08-20")
            "Reaffirmation retains the existing actual date"

        Expect.equal
            (updated.Decision |> Option.map (Decision.date >> CalendarDate.format))
            (Some "2026-08-15")
            "Omitted decision date retained"

        Expect.equal
            (updated.Decision |> Option.map (Decision.payable >> Money.currency))
            (Some "USD")
            "Complete replacement money pair"

        let! chronology =
            execute
                owner
                50
                "PARTIAL-FACTS"
                5L
                (CaseAct.Correct
                    { change with
                        Decision =
                            Change.Update
                                {
                                    Date = Some "2026-08-21"
                                    Payable = None
                                }
                        Payment = PaymentChange.Reaffirm
                    })

        match chronology.Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput issues) ->
            let issues = InputIssues.toList issues
            Expect.equal issues.Length 1 "One invalid relation"

            Expect.equal
                issues.Head.Target
                (InputTarget.Field BusinessField.PaymentDecisionDate)
                "The asserted side is actionable"

            Expect.equal
                (issues.Head.RelatedDate
                 |> Option.map (fun date -> date.Field, CalendarDate.format date.Value))
                (Some(BusinessField.PaymentDate, "2026-08-20"))
                "Retained counterpart context"
        | other -> failtestf "Expected chronology refusal: %A" other

        let! retained = get owner caseId

        Expect.equal
            (read retained).Snapshot.Revision
            (CaseFixtures.revision 5L)
            "Chronology refusal cannot create a revision or silently rebase"
    }
