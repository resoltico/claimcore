module ClaimCore.Tests.RetainedDecisionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.Tests.TaskBuilders

let private recorded number =
    let value = context number

    { value with
        Decision =
            { value.Decision with
                Lifetime = DecisionLifetime.Recorded
            }
    }

let private historical () =
    CaseFamily.apply
        None
        { initialChange with
            Value = PartValue.clear initialChange.Value
        }
    |> accepted

let private familyTask token =
    match Registry.find (TaskId.ofLiteral token) with
    | Some(Task.Family task) -> task
    | _ -> failtest "Expected family task"

let private retained token number state binding inputs =
    (familyTask token).DecideRetained (recorded number) environment (Some state) binding inputs
    |> accepted

let private caseBinding (state: CaseFamily) =
    Binding.Case(target state, state.Case.Revision)

[<Tests>]
let tests =
    testList
        "retained task decisions"
        [
            testCase
                "case tasks reproduce public lifecycle status and revisions without private content"
                (fun () ->
                    let opened = historical ()
                    let closed = setCase Lifecycle.Active CaseStatus.Closed opened
                    let voided = setCase Lifecycle.Voided CaseStatus.Opened opened

                    let cases =
                        [
                            "case.amendRegistration", opened, Lifecycle.Active, CaseStatus.Opened
                            "case.decide", opened, Lifecycle.Active, CaseStatus.Opened
                            "case.withdrawDecision", opened, Lifecycle.Active, CaseStatus.Opened
                            "case.correct", closed, Lifecycle.Active, CaseStatus.Closed
                            "case.void", opened, Lifecycle.Voided, CaseStatus.Opened
                            "case.reinstate", voided, Lifecycle.Active, CaseStatus.Opened
                            "case.close", opened, Lifecycle.Active, CaseStatus.Closed
                            "case.reopen", closed, Lifecycle.Active, CaseStatus.Opened
                        ]

                    for token, before, lifecycle, status in cases do
                        let changes =
                            retained
                                token
                                5
                                (DecisionFacts.fromReplay before)
                                (caseBinding before)
                                (if token = "case.close" then
                                     closingInputs "SETTLED"
                                 else
                                     InputMap.empty)

                        let change = List.exactlyOne changes
                        Expect.equal change.Kind PartKind.Case token

                        Expect.equal
                            change.Revision
                            (Revision.successor before.Case.Revision)
                            token

                        Expect.equal change.Aggregate (AggregateReference.Family before.Id) token
                        Expect.isNone change.Erases token

                        match change.Value with
                        | PartValue.Case value ->
                            Expect.equal value.Lifecycle lifecycle token
                            Expect.equal value.Status status token
                            Expect.isNone value.Content "No private case value is invented"
                            Expect.isNone value.Disposition "No erased reason is invented"
                        | _ -> failtest token)
            testCase
                "payment recording and removal retain separate part identities and revisions"
                (fun () ->
                    let opened = historical ()

                    let changes =
                        retained
                            "case.recordPayment"
                            2
                            (DecisionFacts.fromReplay opened)
                            (caseBinding opened)
                            InputMap.empty

                    Expect.equal
                        (changes |> List.map _.Kind)
                        [ PartKind.Payment; PartKind.Case ]
                        "Ordered accepted parts"

                    let after = apply opened changes
                    let paymentId = operation 2

                    let removed =
                        retained
                            "case.removePayment"
                            3
                            (DecisionFacts.fromReplay after)
                            (Binding.CasePayment(target after, after.Case.Revision, paymentId))
                            InputMap.empty

                    Expect.equal
                        (removed |> List.map _.Revision)
                        [ Revision.create 2L |> Option.get; Revision.create 3L |> Option.get ]
                        "Independent revisions"

                    match removed.Head.Value with
                    | PartValue.Payment value ->
                        Expect.isTrue value.Removed "Public removal"
                        Expect.isNone value.Entry "No fabricated amount/date"
                    | _ -> failtest "Expected payment"

                    let paid = apply after removed

                    Expect.isError
                        ((familyTask "case.removePayment").DecideRetained
                            (recorded 4)
                            environment
                            (Some(DecisionFacts.fromReplay paid))
                            (Binding.CasePayment(target paid, paid.Case.Revision, paymentId))
                            InputMap.empty)
                        "A removed payment stays removed")
            testCase "work keeps its own reviewed version and notes keep removal state" (fun () ->
                let opened = historical ()

                let work =
                    retained
                        "work.update"
                        2
                        (DecisionFacts.fromReplay opened)
                        (Binding.Work(target opened, Revision.zero))
                        InputMap.empty

                Expect.equal (List.exactlyOne work).Kind PartKind.Work "Work part"
                let after = apply opened work

                let repeated =
                    retained
                        "work.update"
                        3
                        (DecisionFacts.fromReplay after)
                        (Binding.Work(target after, Revision.first))
                        InputMap.empty

                Expect.equal
                    repeated.Head.Revision
                    (Revision.create 2L |> Option.get)
                    "Work succession"

                Expect.isError
                    ((familyTask "work.update").DecideRetained
                        (recorded 3)
                        environment
                        (Some(DecisionFacts.fromReplay after))
                        (Binding.Work(target after, Revision.zero))
                        InputMap.empty)
                    "Stale review"

                let note =
                    retained
                        "note.add"
                        3
                        (DecisionFacts.fromReplay after)
                        (Binding.NoteNew(target after))
                        InputMap.empty

                let noted = apply after note

                let removed =
                    retained
                        "note.remove"
                        4
                        (DecisionFacts.fromReplay noted)
                        (Binding.Note(target noted, NoteId.ofOperationId (operation 3)))
                        InputMap.empty

                match removed.Head.Value with
                | PartValue.Note value ->
                    Expect.isTrue value.Removed "Removal"
                    Expect.isNone value.Text "No text invented"
                | _ -> failtest "Expected note")
            testCase
                "reminder retained decisions preserve arcs compound ordering and identity reservation"
                (fun () ->
                    let opened = historical ()

                    let created =
                        retained
                            "reminder.create"
                            2
                            (DecisionFacts.fromReplay opened)
                            (Binding.ReminderNew(target opened))
                            InputMap.empty

                    let pending = apply opened created
                    let id = ReminderId.ofOperationId (operation 2)
                    let bound = Binding.Reminder(target pending, id, Revision.first)

                    let updated =
                        retained
                            "reminder.update"
                            3
                            (DecisionFacts.fromReplay pending)
                            bound
                            InputMap.empty

                    let completed =
                        retained
                            "reminder.complete"
                            3
                            (DecisionFacts.fromReplay pending)
                            bound
                            InputMap.empty

                    let cancelled =
                        retained
                            "reminder.cancel"
                            3
                            (DecisionFacts.fromReplay pending)
                            bound
                            InputMap.empty

                    for changes, expected in
                        [
                            updated, ReminderState.Pending
                            completed, ReminderState.Completed
                            cancelled, ReminderState.Cancelled
                        ] do
                        match changes.Head.Value with
                        | PartValue.Reminder value ->
                            Expect.equal value.State expected "Public state"
                            Expect.isNone value.Plan "No erased action/date invented"
                        | _ -> failtest "Expected reminder"

                    let doneState = apply pending completed

                    let reopened =
                        retained
                            "reminder.reopen"
                            4
                            (DecisionFacts.fromReplay doneState)
                            (Binding.Reminder(
                                target doneState,
                                id,
                                Revision.create 2L |> Option.get
                            ))
                            InputMap.empty

                    match reopened.Head.Value with
                    | PartValue.Reminder value ->
                        Expect.equal value.State ReminderState.Pending "Reopened"
                    | _ -> failtest "Expected reminder"

                    let compound =
                        retained
                            "reminder.completeAndPlanNext"
                            3
                            (DecisionFacts.fromReplay pending)
                            bound
                            InputMap.empty

                    Expect.equal
                        (compound |> List.map _.PartId)
                        [
                            ReminderId.value id
                            ReminderId.value (ReminderId.ofOperationId (operation 3))
                        ]
                        "Existing then created identity"

                    Expect.equal
                        (compound |> List.map (_.Stamp.Position >> EffectPosition.index))
                        [ 0; 1 ]
                        "Real ordered positions"

                    Expect.isError
                        ((familyTask "reminder.update").DecideRetained
                            (recorded 4)
                            environment
                            (Some(DecisionFacts.fromReplay doneState))
                            (Binding.Reminder(
                                target doneState,
                                id,
                                Revision.create 2L |> Option.get
                            ))
                            InputMap.empty)
                        "Completed reminder cannot be updated")
            testCase
                "person creation validates retained principals grants and profile presence"
                (fun () ->
                    let run token inputs =
                        match Registry.find (TaskId.ofLiteral token) with
                        | Some(Task.Person task) ->
                            task.DecideRetained
                                (recorded 2)
                                (peopleEnvironment newPrincipal)
                                None
                                (Binding.PeopleNew Revision.zero)
                                inputs
                            |> accepted
                            |> List.exactlyOne
                        | _ -> failtest "Expected person task"

                    let publicInputs = inputs (principalInputs newPrincipal)
                    let registered = run "people.register" publicInputs

                    match registered.Value with
                    | PartValue.Person value ->
                        Expect.equal value.Principal (Some newPrincipal) "Public identity"

                        Expect.equal
                            value.Profile
                            (ProfileValue.Present None)
                            "Known presence, unavailable bytes"

                        Expect.isTrue value.Enabled "Creation is enabled"
                        Expect.isEmpty value.Grants "Registration does not invent roles"
                    | _ -> failtest "Expected person"

                    let grantRows =
                        Map.ofList
                            [
                                TaskCollectionId.PeopleGrants,
                                [
                                    Map.ofList
                                        [
                                            text InputTarget.Role "CASE_READER"
                                            TaskInputId.Scope, InputValue.Text "INSTALLATION"
                                        ]
                                ]
                            ]

                    let admitted =
                        run
                            "people.admit"
                            (InputMap.create publicInputs.Values grantRows |> Option.get)

                    match admitted.Value with
                    | PartValue.Person value ->
                        Expect.equal
                            value.Grants
                            [
                                {
                                    Grant =
                                        {
                                            Role = Role.CaseReader
                                            Scope = Scope.Installation
                                        }
                                    Active = true
                                }
                            ]
                            "Retained assignments"
                    | _ -> failtest "Expected person")
            testCase "retained grant resolution uses case-reference identity" (fun () ->
                let state = person ()
                let family = historical ()

                let scopeEnvironment =
                    { peopleEnvironment newPrincipal with
                        TargetCases = Map.ofList [ GrantTarget.CaseReference "TASK-1", family.Id ]
                    }

                let publicInput =
                    inputs
                        [
                            text InputTarget.Role "CASE_READER"
                            TaskInputId.Scope, InputValue.Text "CASE"
                            text (InputTarget.Field BusinessField.CaseReference) "task-1"
                        ]

                let selected =
                    match Registry.find (TaskId.ofLiteral "people.grant") with
                    | Some(Task.Person task) -> task
                    | _ -> failtest "Expected person task"

                let changed =
                    selected.DecideRetained
                        (recorded 3)
                        scopeEnvironment
                        (Some state)
                        (personBinding state)
                        publicInput
                    |> accepted
                    |> List.exactlyOne

                match changed.Value with
                | PartValue.Person value ->
                    Expect.equal
                        value.Grants
                        [
                            {
                                Grant =
                                    {
                                        Role = Role.CaseReader
                                        Scope = Scope.Case family.Id
                                    }
                                Active = true
                            }
                        ]
                        "Same retained case identity"
                | _ -> failtest "Expected person")
        ]
