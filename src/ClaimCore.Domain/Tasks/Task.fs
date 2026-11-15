namespace ClaimCore.Domain

open System
open System.Text.RegularExpressions

/// A task token is an identity; registry membership is resolved independently.
[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type TaskId = private TaskId of string

module TaskId =
    let token (TaskId value) = value

    let tryParse value =
        if
            not (String.IsNullOrEmpty value)
            && Regex.IsMatch(value, @"\A[a-z]+(\.[a-z][A-Za-z]*)+\z", RegexOptions.CultureInvariant)
        then
            Some(TaskId value)
        else
            None

    let ofLiteral value =
        match tryParse value with
        | Some task -> task
        | None -> invalidArg (nameof value) "Use a dotted task token."

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

namespace ClaimCore.Domain.Tasks

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
        Editable: TaskInputId list
        Choices: Map<TaskInputId, string list>
        Blocked: Refusal option
    }

[<NoEquality; NoComparison>]
type FamilyTask =
    {
        Declaration: TaskDeclaration
        Check: InputMap -> TaskInputIssue list
        Availability: FamilyEnvironment -> CaseFamily option -> Binding -> Availability
        DecisionFactsAvailable: CaseFamily option -> Binding -> bool
        Describe:
            CaseFamily option
                -> Map<NoteId, NoteValue>
                -> PartChange list
                -> Result<DescribedPart list, RecordViolation>
        Decide:
            TaskContext
                -> FamilyEnvironment
                -> CaseFamily option
                -> Binding
                -> InputMap
                -> Result<PartChange list, Rejection>
    }

[<NoEquality; NoComparison>]
type PersonTask =
    {
        Declaration: TaskDeclaration
        Check: InputMap -> TaskInputIssue list
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
    }

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type Task =
    | Family of FamilyTask
    | Person of PersonTask

module Task =
    let declaration =
        function
        | Task.Family task -> task.Declaration
        | Task.Person task -> task.Declaration
