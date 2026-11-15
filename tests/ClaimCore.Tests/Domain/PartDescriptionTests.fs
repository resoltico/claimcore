module ClaimCore.Tests.PartDescriptionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.Tests.TaskBuilders

let private familyTask =
    function
    | Task.Family task -> task
    | Task.Person _ -> failtest "Expected family task"

let private personTask =
    function
    | Task.Person task -> task
    | Task.Family _ -> failtest "Expected person task"

[<Tests>]
let tests =
    testList
        "accepted part descriptions"
        [
            testCase
                "case creation and a status-only change share field and money differences"
                (fun () ->
                    let task = ClaimCore.Domain.Tasks.Case.Open.task |> familyTask

                    let description =
                        task.Describe None Map.empty [ initialChange ]
                        |> accepted
                        |> List.exactlyOne

                    match description.Description with
                    | PartDescription.Case change ->
                        Expect.equal
                            change.Lifecycle
                            (Some
                                {
                                    Before = None
                                    After = Some Lifecycle.Active
                                })
                            "Creation lifecycle"

                        Expect.isTrue
                            (change.Fields
                             |> List.exists (fun field ->
                                 field.FieldName = BusinessField.CaseReference
                                 && field.Before.IsNone
                                 && field.After = Some(CaseFieldValue.Reference reference)))
                            "Retained identity"

                        let claimed =
                            change.Money
                            |> List.find (fun change ->
                                change.FieldName = BusinessField.ClaimedAmount)

                        Expect.equal
                            (claimed.Change.After |> Option.map Money.value)
                            (Some 1000M)
                            "Typed complete money"
                    | _ -> failtest "Expected case description"

                    let state = family ()
                    let task = ClaimCore.Domain.Tasks.Case.Close.task |> familyTask

                    let changes =
                        task.Decide
                            (context 2)
                            environment
                            (Some state)
                            (caseBinding state)
                            InputMap.empty
                        |> accepted

                    match
                        (task.Describe (Some state) Map.empty changes
                         |> accepted
                         |> List.exactlyOne)
                            .Description
                    with
                    | PartDescription.Case change ->
                        Expect.equal
                            (change.Fields |> List.map _.FieldName)
                            [ BusinessField.Status ]
                            "Only the status changed"

                        Expect.isEmpty change.Money "No repeated financial narrative"
                    | _ -> failtest "Expected case description")
            testCase
                "payment descriptions retain entry details and the case's financial delta"
                (fun () ->
                    let state = decided ()
                    let task = ClaimCore.Domain.Tasks.Case.RecordPayment.task |> familyTask

                    let changes =
                        task.Decide
                            (context 3)
                            environment
                            (Some state)
                            (caseBinding state)
                            (paymentInputs "2026-02-02" "25.50" "EUR")
                        |> accepted

                    let descriptions = task.Describe (Some state) Map.empty changes |> accepted

                    match descriptions |> List.map _.Description with
                    | [ PartDescription.Payment entry; PartDescription.Case finance ] ->
                        Expect.equal
                            (entry.Entry |> Option.map PaymentEntry.id)
                            (Some(operation 3))
                            "Accepted payment identity"

                        Expect.equal
                            entry.Removed
                            (Some { Before = None; After = Some false })
                            "New live entry"

                        let paid =
                            finance.Money
                            |> List.find (fun field -> field.FieldName = BusinessField.PaidAmount)

                        Expect.equal
                            (paid.Change.Before |> Option.map Money.value,
                             paid.Change.After |> Option.map Money.value)
                            (Some 0M, Some 25.50M)
                            "Before and after financial context"
                    | _ -> failtest "Expected the entry and financial part descriptions")
            testCase "work describes independent assignment and summary changes" (fun () ->
                let state = family ()
                let task = ClaimCore.Domain.Tasks.Work.Update.task |> familyTask

                let changes =
                    task.Decide
                        (context 2)
                        environment
                        (Some state)
                        (Binding.Work(target state, Revision.zero))
                        (inputs
                            [
                                text
                                    (InputTarget.Field BusinessField.ResponsibleActor)
                                    (ActorId.format actorId)
                                text (InputTarget.Field BusinessField.HandlingSummary) "Summary"
                            ])
                    |> accepted

                match
                    (task.Describe (Some state) Map.empty changes |> accepted |> List.exactlyOne)
                        .Description
                with
                | PartDescription.Work change ->
                    Expect.equal
                        change.Responsible
                        (Some { Before = None; After = Some actorId })
                        "Identity remains separate from current display labels"

                    Expect.equal
                        (change.Summary |> Option.bind _.After |> Option.map HandlingSummary.format)
                        (Some "Summary")
                        "Accepted narrative"
                | _ -> failtest "Expected work description")
            testCase
                "terminal reminder descriptions omit unchanged action and due values"
                (fun () ->
                    let state, id = reminder ()
                    let task = ClaimCore.Domain.Tasks.Reminder.Complete.task |> familyTask

                    let changes =
                        task.Decide
                            (context 3)
                            environment
                            (Some state)
                            (reminderBinding state id Revision.first)
                            InputMap.empty
                        |> accepted

                    match
                        (task.Describe (Some state) Map.empty changes
                         |> accepted
                         |> List.exactlyOne)
                            .Description
                    with
                    | PartDescription.Reminder change ->
                        Expect.equal
                            change.State
                            (Some
                                {
                                    Before = Some ReminderState.Pending
                                    After = Some ReminderState.Completed
                                })
                            "Selected state transition"

                        Expect.isNone change.Action "The action was not replaced"
                        Expect.isNone change.Due "The due date was not replaced"
                    | _ -> failtest "Expected reminder description")
            testCase
                "note removal obtains preceding text separately from aggregate decision state"
                (fun () ->
                    let state = family ()
                    let textValue = NoteText.parse "Preceding accepted text" |> accepted

                    let add =
                        ClaimCore.Domain.Tasks.Note.Add.decide
                            (context 2)
                            environment
                            (Some state)
                            (Binding.NoteNew(target state))
                            (inputs [ text InputTarget.NoteText (NoteText.format textValue) ])
                        |> accepted

                    let state = apply state add
                    let id = NoteId.ofOperationId (operation 2)
                    let task = ClaimCore.Domain.Tasks.Note.Remove.task |> familyTask

                    let changes =
                        task.Decide
                            (context 3)
                            environment
                            (Some state)
                            (Binding.Note(target state, id))
                            InputMap.empty
                        |> accepted

                    Expect.isError
                        (task.Describe (Some state) Map.empty changes)
                        "The description cannot silently drop required preceding values"

                    let prior =
                        Map.ofList
                            [
                                id,
                                {
                                    NoteValue.Removed = false
                                    Text = Some textValue
                                }
                            ]

                    match
                        (task.Describe (Some state) prior changes |> accepted |> List.exactlyOne)
                            .Description
                    with
                    | PartDescription.Note change ->
                        Expect.equal
                            change.Text
                            (Some
                                {
                                    Before = Some textValue
                                    After = None
                                })
                            "Exact removal context"
                    | _ -> failtest "Expected note description")
            testCase
                "person descriptions use accepted sparse assignments and profile differences"
                (fun () ->
                    let state = person ()
                    let task = ClaimCore.Domain.Tasks.People.Grant.task |> personTask

                    let changes =
                        task.Decide
                            (context 3)
                            (peopleEnvironment newPrincipal)
                            (Some state)
                            (personBinding state)
                            (grantInputs Role.CaseReader "INSTALLATION")
                        |> accepted

                    match
                        (task.Describe (Some state) changes |> accepted |> List.exactlyOne)
                            .Description
                    with
                    | PartDescription.Person change ->
                        Expect.equal
                            change.Grants
                            [
                                {
                                    Grant =
                                        {
                                            Role = Role.CaseReader
                                            Scope = Scope.Installation
                                        }
                                    Before = false
                                    After = true
                                }
                            ]
                            "Only the accepted assignment is described"

                        Expect.isNone change.Principal "Identity did not change"
                        Expect.isNone change.DisplayName "The profile did not change"
                    | _ -> failtest "Expected person description"

                    let task = ClaimCore.Domain.Tasks.People.EraseProfile.task |> personTask

                    let changes =
                        task.Decide
                            (context 3)
                            (peopleEnvironment newPrincipal)
                            (Some state)
                            (personBinding state)
                            InputMap.empty
                        |> accepted

                    match
                        (task.Describe (Some state) changes |> accepted |> List.exactlyOne)
                            .Description
                    with
                    | PartDescription.Person change ->
                        Expect.equal
                            (change.DisplayName
                             |> Option.bind _.Before
                             |> Option.map PartyName.format)
                            (Some "New Person")
                            "Prior profile"

                        Expect.equal
                            (change.DisplayName |> Option.bind _.After)
                            None
                            "Erased profile"

                        Expect.isEmpty change.Grants "No rights were changed"
                    | _ -> failtest "Expected person description")
            testCase
                "re-decision distinguishes empty available inputs from erased required payment facts"
                (fun () ->
                    let state = family ()

                    let decisionChanges =
                        ClaimCore.Domain.Tasks.Case.Decide.decide
                            (context 2)
                            environment
                            (Some state)
                            (caseBinding state)
                            (decisionInputs "2026-02-01" "1000" "EUR")
                        |> accepted

                    let decided = apply state decisionChanges

                    let paymentChanges =
                        ClaimCore.Domain.Tasks.Case.RecordPayment.decide
                            (context 3)
                            environment
                            (Some decided)
                            (caseBinding decided)
                            (paymentInputs "2026-02-02" "25" "EUR")
                        |> accepted

                    let cleared (change: PartChange) =
                        { change with
                            Value = PartValue.clear change.Value
                        }

                    let initial = CaseFamily.apply None (cleared initialChange) |> accepted

                    let historical =
                        apply
                            initial
                            (List.map cleared decisionChanges @ List.map cleared paymentChanges)

                    let remove = ClaimCore.Domain.Tasks.Case.RemovePayment.task |> familyTask

                    Expect.isError
                        (CaseFamily.rows historical [])
                        "Unavailable active content cannot become a fabricated projection"

                    let binding =
                        Binding.CasePayment(
                            target historical,
                            historical.Case.Revision,
                            operation 3
                        )

                    Expect.isEmpty
                        (remove.Check InputMap.empty)
                        "The task has no authored inputs to lose"

                    Expect.isFalse
                        (remove.DecisionFactsAvailable (Some historical) binding)
                        "Private amounts and date bounds are still necessary"

                    let close = ClaimCore.Domain.Tasks.Case.Close.task |> familyTask

                    Expect.isTrue
                        (close.DecisionFactsAvailable (Some historical) (caseBinding historical))
                        "A retained status transition needs no private financial facts"

                    let changes =
                        close.Decide
                            (context 4)
                            environment
                            (Some historical)
                            (caseBinding historical)
                            InputMap.empty
                        |> accepted

                    match changes.Head.Value with
                    | PartValue.Case value ->
                        Expect.equal
                            value.Status
                            CaseStatus.Closed
                            "The retained outcome is reproduced"

                        Expect.isNone value.Content "Missing private values are never fabricated"
                    | _ -> failtest "Expected case value"

                    let erasure =
                        ClaimCore.Domain.Tasks.Case.Erase.decide
                            (context 4)
                            environment
                            (Some historical)
                            (Binding.CaseErase(
                                target historical,
                                historical.Case.Revision,
                                historical.Sequence
                            ))
                            InputMap.empty
                        |> accepted

                    let erased = apply historical erasure
                    let _, index = CaseFamily.rows erased erasure |> accepted

                    Expect.isNone
                        index.Paid
                        "Erasure yields unavailable money, not a fabricated zero"

                    Expect.isNone index.Claimed "No private claim is reconstructed")
            testCase
                "cleared bytes from a later profile replacement cannot reuse an earlier erasure stamp"
                (fun () ->
                    let state = person ()
                    let environment = peopleEnvironment newPrincipal

                    let erased =
                        ClaimCore.Domain.Tasks.People.EraseProfile.decide
                            (context 3)
                            environment
                            (Some state)
                            (personBinding state)
                            InputMap.empty
                        |> accepted
                        |> personApply state

                    let replacement =
                        ClaimCore.Domain.Tasks.People.SetProfile.decide
                            (context 4)
                            environment
                            (Some erased)
                            (personBinding erased)
                            (inputs profileInputs)
                        |> accepted
                        |> List.exactlyOne

                    let cleared =
                        { replacement with
                            Value = PartValue.clear replacement.Value
                        }

                    let historical = Person.apply (Some erased) cleared |> accepted

                    Expect.isNone
                        historical.ProfileErasure
                        "A replacement existed at this prefix even though a later erasure removed its bytes"

                    Expect.isError
                        (Person.rows historical)
                        "Unavailable replacement bytes are distinct from an absent profile"

                    let nextErasure =
                        ClaimCore.Domain.Tasks.People.EraseProfile.decide
                            (context 5)
                            environment
                            (Some historical)
                            (personBinding historical)
                            InputMap.empty
                        |> accepted
                        |> personApply historical

                    Expect.equal
                        nextErasure.ProfileErasure
                        (Some((context 5).Stamp.Position))
                        "The later erasure has its own accepting position"

                    Expect.isOk
                        (Person.rows nextErasure)
                        "An explicit absent profile can be projected")
        ]
