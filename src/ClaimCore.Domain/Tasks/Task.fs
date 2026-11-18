namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type TaskExposure =
    | Client
    | Operator

[<RequireQualifiedAccess>]
type AggregateKind =
    | Family
    | Person

/// Business declarations are owned by the task, independently of any client or transport.
type TaskDeclaration =
    {
        Id: TaskId
        Exposure: TaskExposure
        Aggregate: AggregateKind
        Target: ActSubject
        Creates: bool
        Family: ActFamily
        Consequence: Consequence
        ReviewsFamilySequence: bool
        // Potential capability needs; input-dependent requirements stay with the task.
        Capabilities: Capability list
        Inputs: TaskInputDeclaration list
        Collections: TaskCollectionDeclaration list
    }

    member this.RequiresHumanRequester =
        not this.Capabilities.IsEmpty
        && List.forall Access.humanOnlyCapability this.Capabilities

module TaskDeclaration =
    let bindingShape (declaration: TaskDeclaration) =
        let binds input =
            declaration.Inputs
            |> List.exists (fun value ->
                value.Input = TaskInputId.Target input && value.Source = InputSource.Binding)

        match declaration.Target, declaration.Creates with
        | ActSubject.Case, true -> BindingShape.CaseNew
        | ActSubject.Case, false when binds InputTarget.PaymentId -> BindingShape.CasePayment
        | ActSubject.Case, false when declaration.ReviewsFamilySequence -> BindingShape.CaseErase
        | ActSubject.Case, false -> BindingShape.Case
        | ActSubject.Work, _ -> BindingShape.Work
        | ActSubject.Reminder, true -> BindingShape.ReminderNew
        | ActSubject.Reminder, false -> BindingShape.Reminder
        | ActSubject.Note, true -> BindingShape.NoteNew
        | ActSubject.Note, false -> BindingShape.Note
        | ActSubject.Party, true -> BindingShape.PartyNew
        | ActSubject.Party, false -> BindingShape.Party
        | ActSubject.Link, true -> BindingShape.LinkNew
        | ActSubject.Link, false -> BindingShape.Link
        | ActSubject.People, true -> BindingShape.PeopleNew
        | ActSubject.People, false -> BindingShape.People

namespace ClaimCore.Domain

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes

[<RequireQualifiedAccess>]
type Rejection =
    | Input of TaskInputIssues
    | Refused of Refusal
    | InvalidState of RecordViolation

module Rejection =
    let ofRefusal =
        function
        | Refusal.InvalidInput issues ->
            Rejection.Input(
                InputIssues.toList issues
                |> List.map TaskInputIssue.ofDomain
                |> TaskInputIssues.ofList
                |> Option.get
            )
        | refusal -> Rejection.Refused refusal

/// Decisions need both current admission facts and the accepting operation's explicit attribution.
[<NoComparison>]
type TaskContext =
    {
        Decision: DecisionContext
        Stamp: SubjectStamp
    }

module TaskContext =
    let changes (context: TaskContext) (values: PartChange list) =
        values
        |> List.mapi (fun index change ->
            let position =
                EffectPosition.create (EffectPosition.sequence context.Stamp.Position) index
                |> Option.get

            { change with
                Stamp =
                    { context.Stamp with
                        Position = position
                    }
            })

type Availability =
    {
        Seeds: Map<TaskInputId, InputValue>
        // Fixed authored selections supplied by this task for current rows.
        Selections: Set<Map<TaskInputId, InputValue>>
        Editable: TaskInputId list
        Choices: Map<TaskInputId, string list>
        Blocked: Refusal option
        Recommendation: int option
    }

[<NoEquality; NoComparison>]
type FamilyTask =
    {
        Declaration: TaskDeclaration
        Check: InputMap -> TaskInputIssue list
        Authorize: PrincipalKey -> AccessView -> CaseId option -> InputMap -> Result<unit, Refusal>
        Availability: FamilyEnvironment -> DecisionFacts option -> Binding -> Availability
        DecisionFactsAvailable: DecisionFacts option -> Binding -> bool
        Describe:
            DecisionFacts option
                -> Map<NoteId, NoteValue>
                -> PartChange list
                -> Result<DescribedPart list, RecordViolation>
        Decide:
            TaskContext
                -> FamilyEnvironment
                -> DecisionFacts option
                -> Binding
                -> InputMap
                -> Result<PartChange list, Rejection>
        DecideRetained:
            TaskContext
                -> FamilyEnvironment
                -> DecisionFacts option
                -> Binding
                -> InputMap
                -> Result<PartChange list, Rejection>
    }

[<NoEquality; NoComparison>]
type PersonTask =
    {
        Declaration: TaskDeclaration
        Check: InputMap -> TaskInputIssue list
        Authorize: PrincipalKey -> AccessView -> InputMap -> Result<unit, Refusal>
        Availability: PersonEnvironment -> Person option -> Binding -> Availability
        DecisionFactsAvailable: Person option -> Binding -> bool
        Describe: Person option -> PartChange list -> Result<DescribedPart list, RecordViolation>
        Decide:
            TaskContext
                -> PersonEnvironment
                -> Person option
                -> Binding
                -> InputMap
                -> Result<PartChange list, Rejection>
        DecideRetained:
            TaskContext
                -> PersonEnvironment
                -> Person option
                -> Binding
                -> InputMap
                -> Result<PartChange list, Rejection>
    }

namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type Task =
    | Family of FamilyTask
    | Person of PersonTask

module Task =
    let declaration =
        function
        | Task.Family task -> task.Declaration
        | Task.Person task -> task.Declaration
