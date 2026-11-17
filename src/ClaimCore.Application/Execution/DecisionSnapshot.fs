namespace ClaimCore.Application.Execution

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks

[<RequireQualifiedAccess>]
type internal AggregateState =
    | Family of DecisionFacts option
    | Person of Person option

/// The state and environment are captured from the same transaction or historical fold position.
[<NoEquality; NoComparison>]
type internal DecisionSnapshot =
    {
        Declaration: TaskDeclaration
        Requester: AccessView
        // Current retained headers plus the acquired private values for selected descriptions.
        Parts: PartChange list
        Origins: Map<PartKind * System.Guid, SubjectStamp>
        State: AggregateState
        Decide: TaskContext -> Binding -> InputMap -> Result<PartChange list, Rejection>
        DecideRetained: TaskContext -> Binding -> InputMap -> Result<PartChange list, Rejection>
        Authorize: InputMap -> Result<unit, Refusal>
        Availability: Binding -> Availability
        FactsAvailable: Binding -> bool
        Describe: PartChange list -> Result<DescribedPart list, RecordViolation>
    }

module internal DecisionSnapshot =
    let family (task: FamilyTask) (environment: FamilyEnvironment) state parts origins =
        {
            Declaration = task.Declaration
            Requester = environment.Requester
            Parts = parts
            Origins = origins
            State = AggregateState.Family state
            Decide =
                fun context binding inputs -> task.Decide context environment state binding inputs
            DecideRetained =
                fun context binding inputs ->
                    task.DecideRetained context environment state binding inputs
            Authorize = task.Authorize environment state
            Availability = task.Availability environment state
            FactsAvailable = task.DecisionFactsAvailable state
            Describe = task.Describe state (PartDescriptions.notes parts)
        }

    let person (task: PersonTask) (environment: PersonEnvironment) state =
        {
            Declaration = task.Declaration
            Requester = environment.Requester
            Parts = []
            Origins = Map.empty
            State = AggregateState.Person state
            Decide =
                fun context binding inputs -> task.Decide context environment state binding inputs
            DecideRetained =
                fun context binding inputs ->
                    task.DecideRetained context environment state binding inputs
            Authorize = task.Authorize environment state
            Availability = task.Availability environment state
            FactsAvailable = task.DecisionFactsAvailable state
            Describe = task.Describe state
        }

    let apply snapshot changes =
        match snapshot.State with
        | AggregateState.Family state ->
            DecisionFacts.apply state changes |> Result.map (Some >> AggregateState.Family)
        | AggregateState.Person state ->
            changes
            |> List.fold
                (fun result change ->
                    result
                    |> Result.bind (fun state -> Person.apply state change |> Result.map Some))
                (Ok state)
            |> Result.map AggregateState.Person
