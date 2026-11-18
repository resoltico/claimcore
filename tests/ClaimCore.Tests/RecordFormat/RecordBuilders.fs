module internal ClaimCore.Tests.RecordBuilders

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.RecordFormat

module Tasks = ClaimCore.Tests.TaskBuilders

let required result =
    result
    |> Result.defaultWith (fun problem -> failtestf "Record witness: %A" problem)

let installation = Guid.Parse "00000000-0000-4000-8000-000000000001"

let header (context: TaskContext) aggregate =
    {
        OperationHeader.InstallationId = installation
        Sequence = EffectPosition.sequence context.Stamp.Position
        PreviousHash = EntryHash.zero
        OperationId = context.Stamp.OperationId
        Authority = context.Stamp.Authority
        RecordedAt = context.Stamp.RecordedAt
        BusinessDate =
            if aggregate = AggregateKind.Family then
                context.Decision.BusinessDate
            else
                None
    }

let create token binding inputs changes =
    let task = Registry.find (TaskId.ofLiteral token) |> Option.get
    let first: PartChange = List.head changes

    let context =
        { (Tasks.context 1) with
            Stamp = first.Stamp
        }

    RetainedOperation.create
        (header context (Task.declaration task).Aggregate)
        (TaskId.ofLiteral token)
        binding
        inputs
        changes
        (fun () ->
            Array.create
                32
                (byte (Sequence.value (EffectPosition.sequence first.Stamp.Position) % 256L)))
    |> required

let family token number (state: CaseFamily) binding inputs =
    match Registry.find (TaskId.ofLiteral token) with
    | Some(Task.Family task) ->
        let changes =
            task.Decide
                (Tasks.context number)
                Tasks.environment
                (Some(DecisionFacts.fromReplay state))
                binding
                inputs
            |> required

        create token binding inputs changes
    | _ -> failtest "Expected family task."

let person token number state binding inputs =
    match Registry.find (TaskId.ofLiteral token) with
    | Some(Task.Person task) ->
        let environment = Tasks.peopleEnvironment Tasks.newPrincipal

        let changes =
            task.Decide (Tasks.context number) environment state binding inputs |> required

        create token binding inputs changes
    | _ -> failtest "Expected person task."

let caseRecord () =
    create "case.open" (Binding.CaseNew Revision.zero) Tasks.openingInputs [ Tasks.initialChange ]

let personRecord () =
    person
        "people.register"
        2
        None
        (Binding.PeopleNew Revision.zero)
        (Tasks.peopleInputs Tasks.newPrincipal)

let workRecord () =
    let state = Tasks.family ()

    family
        "work.update"
        2
        state
        (Binding.Work(Tasks.target state, Revision.zero))
        (Tasks.inputs
            [
                Tasks.text (InputTarget.Field BusinessField.HandlingSummary) "Private summary"
            ])

let noteRecord () =
    let state = Tasks.family ()

    family
        "note.add"
        2
        state
        (Binding.NoteNew(Tasks.target state))
        (Tasks.inputs [ Tasks.text InputTarget.NoteText "Private note" ])

let noteRemoval () =
    let added = noteRecord ()
    let state = Tasks.apply (Tasks.family ()) added.Changes

    family
        "note.remove"
        3
        state
        (Binding.Note(Tasks.target state, NoteId.ofOperationId added.Root.Header.OperationId))
        InputMap.empty

let paymentRecord () =
    let state = Tasks.decided ()

    family
        "case.recordPayment"
        3
        state
        (Tasks.caseBinding state)
        (Tasks.paymentInputs "2026-03-01" "100" "EUR")

let paymentRemoval () =
    let paid = paymentRecord ()
    let state = Tasks.apply (Tasks.decided ()) paid.Changes

    family
        "case.removePayment"
        4
        state
        (Binding.CasePayment(Tasks.target state, state.Case.Revision, paid.Root.Header.OperationId))
        InputMap.empty

let reminderRecord () =
    let state = Tasks.family ()

    family
        "reminder.create"
        2
        state
        (Binding.ReminderNew(Tasks.target state))
        (Tasks.planInputs "Call insurer" "2026-11-16")

let compoundReminder () =
    let state, reminder = Tasks.reminder ()

    family
        "reminder.completeAndPlanNext"
        3
        state
        (Binding.Reminder(Tasks.target state, reminder, Revision.first))
        (Tasks.planInputs "Contact insurer" "2026-11-17")

let erasedCase () =
    let state = Tasks.family ()

    family
        "case.erase"
        2
        state
        (Binding.CaseErase(Tasks.target state, state.Case.Revision, state.Sequence))
        InputMap.empty

let disabledPerson () =
    let state = Tasks.person ()
    person "people.disable" 3 (Some state) (Tasks.personBinding state) InputMap.empty

let samples () =
    [
        caseRecord ()
        paymentRecord ()
        paymentRemoval ()
        workRecord ()
        noteRecord ()
        noteRemoval ()
        reminderRecord ()
        compoundReminder ()
        personRecord ()
        disabledPerson ()
        erasedCase ()
    ]

let restore (root: OperationRoot) =
    OperationRoot.restore
        root.Header
        (TaskId.token root.Structure.Task)
        (OperationRoot.structureBytes root)
        (OperationRoot.effectBytes root)
        root.Structure.Compartments
        root.Hash

let clear (record: RetainedOperation) =
    let root = record.Root

    OperationRoot.restore
        root.Header
        (TaskId.token root.Structure.Task)
        (OperationRoot.structureBytes root)
        (OperationRoot.effectBytes root)
        (root.Structure.Compartments |> List.map Compartment.withoutContent)
        root.Hash
    |> required
    |> RetainedOperation.restore
    |> required

let rewriteCompartments compartments (record: RetainedOperation) =
    let root = record.Root

    OperationRoot.append
        root.Header
        root.Structure.Task
        root.Structure.Binding
        root.Structure.Retained
        root.Structure.Effects
        compartments

// A public profile-erasure fragment, independent of private codecs and of an actual fold prefix.
let publicRecord () =
    let operation number =
        Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)
        |> OperationId.create
        |> Option.get

    let actor number =
        ActorId.ofOperationId (operation number)

    let sequence = Sequence.create 3L |> Option.get

    let header =
        {
            OperationHeader.InstallationId = installation
            Sequence = sequence
            PreviousHash = EntryHash.ofBytes (Array.create 32 2uy) |> Option.get
            OperationId = operation 3
            Authority = Authority.Actor(actor 1)
            RecordedAt = DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
            BusinessDate = None
        }

    let change =
        {
            PartChange.Aggregate = AggregateReference.Person(actor 2)
            PartId = ActorId.value (actor 2)
            Revision = Revision.create 2L |> Option.get
            NaturalKey = None
            Value =
                PartValue.Person
                    {
                        Principal = None
                        Enabled = true
                        Profile = ProfileValue.Absent
                        Grants = []
                    }
            Erases = Some(CompartmentOwner.Profile(actor 2))
            Stamp =
                {
                    OperationId = header.OperationId
                    Authority = header.Authority
                    RecordedAt = header.RecordedAt
                    Position = EffectPosition.create sequence 0 |> Option.get
                }
        }

    RetainedOperation.create
        header
        (TaskId.ofLiteral "people.eraseProfile")
        (Binding.People(actor 2, Revision.first))
        InputMap.empty
        [ change ]
        (fun () -> failtest "A public erasure does not request a nonce.")
    |> required
