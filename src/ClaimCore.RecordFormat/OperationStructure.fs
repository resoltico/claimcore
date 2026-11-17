namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes

/// Public means retained structural metadata, never anonymous API disclosure.
type OperationHeader =
    {
        InstallationId: Guid
        Sequence: Sequence
        PreviousHash: EntryHash
        OperationId: OperationId
        ActorId: ActorId
        RecordedAt: DateTimeOffset
        BusinessDate: DateOnly option
    }

/// The public value has no private bytes; its compartment supplies them when available.
type RecordedPart =
    {
        Change: PartChange
        Compartment: int option
    }

[<RequireQualifiedAccess>]
type OperationStructureProblem =
    | Header
    | Task
    | Binding
    | Inputs
    | Effects
    | Compartments
    | CompartmentBinding
    | UnavailableAppendContent

type OperationStructure =
    private
        {
            HeaderValue: OperationHeader
            TaskValue: TaskId
            BindingValue: Binding
            RetainedValue: InputMap
            EffectsValue: RecordedPart list
            CompartmentsValue: Compartment list
        }

    member this.Header = this.HeaderValue
    member this.Task = this.TaskValue
    member this.Binding = this.BindingValue
    member this.Retained = this.RetainedValue
    member this.Effects = this.EffectsValue
    member this.Compartments = this.CompartmentsValue

module OperationStructure =
    [<Literal>]
    let MaximumEffects = 50

    [<Literal>]
    let MaximumCompartments = 2

    let internal validHeader (header: OperationHeader) =
        header.InstallationId <> Guid.Empty
        && header.Sequence >= Sequence.first
        && RecordedInstant.hasMicrosecondPrecision header.RecordedAt

    let create
        header
        task
        binding
        retained
        (effects: RecordedPart list)
        (compartments: Compartment list)
        =
        if not (validHeader header) then
            Error OperationStructureProblem.Header
        elif effects.IsEmpty || effects.Length > MaximumEffects then
            Error OperationStructureProblem.Effects
        else
            let first = effects.Head.Change
            let owner = PartChange.owner first

            let sameHeader index (part: RecordedPart) =
                let stamp = part.Change.Stamp

                stamp.OperationId = header.OperationId
                && stamp.ActorId = header.ActorId
                && stamp.RecordedAt = header.RecordedAt
                && EffectPosition.sequence stamp.Position = header.Sequence
                && EffectPosition.index stamp.Position = index

            let effectsValid =
                effects |> List.mapi sameHeader |> List.forall id
                && (effects
                    |> List.forall (fun part ->
                        part.Change.Aggregate = first.Aggregate
                        && part.Change.PartId <> Guid.Empty
                        && part.Change.Revision >= Revision.first
                        && PartChange.validErasure part.Change
                        && (part.Change.NaturalKey |> Option.forall UnicodeText.isPersisted)))

            let dated =
                match first.Aggregate with
                | AggregateReference.Family _ -> header.BusinessDate.IsSome
                | AggregateReference.Person _ -> header.BusinessDate.IsNone

            let descriptors = compartments |> List.map _.Descriptor
            let ordinals = descriptors |> List.map _.Ordinal

            let compartmentsValid =
                compartments.Length <= MaximumCompartments
                && ordinals = [ 0 .. compartments.Length - 1 ]
                && (descriptors |> List.map _.Owner |> List.distinct |> List.length) =
                    compartments.Length
                && (descriptors |> List.forall (fun descriptor -> descriptor.Owner = owner))
                && Compartment.distinctAvailableNonces compartments

            let referencesValid =
                effects
                |> List.forall (fun part ->
                    PartCodecs.hasPrivate part.Change.Value = part.Compartment.IsSome
                    && (part.Compartment
                        |> Option.forall (fun index ->
                            descriptors
                            |> List.tryItem index
                            |> Option.exists (fun descriptor ->
                                descriptor.Owner = PartChange.owner part.Change))))

            if not effectsValid || not dated then
                Error OperationStructureProblem.Effects
            elif not compartmentsValid then
                Error OperationStructureProblem.Compartments
            elif not referencesValid then
                Error OperationStructureProblem.CompartmentBinding
            else
                Ok
                    {
                        HeaderValue = header
                        TaskValue = task
                        BindingValue = binding
                        RetainedValue = retained
                        EffectsValue = effects
                        CompartmentsValue = compartments
                    }

    let append header task binding retained effects compartments =
        if
            compartments
            |> List.exists (fun value -> Compartment.content value |> Option.isNone)
        then
            Error OperationStructureProblem.UnavailableAppendContent
        else
            create header task binding retained effects compartments
