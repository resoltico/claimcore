namespace ClaimCore.Application.Execution

open System
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal FoldProblem =
    | Link
    | Identity
    | Task
    | Executor of ExecutorIssue
    | Decision
    | State of RecordViolation
    | NaturalKey
    | Nonce
    | Erasure
    | ErasureIncomplete
    | TargetHumanRequired

/// The fold retains decision state and accepted part values, never a second task interpreter.
type internal FoldState =
    {
        Head: LedgerPosition
        Families: Map<CaseId, CaseFamily>
        People: Map<ActorId, Person>
        Parts: Map<PartKind * Guid, PartChange>
        Origins: Map<PartKind * Guid, SubjectStamp>
        Summaries: Map<CaseId, Attribution>
        ReportDates: Map<CaseId, CaseReportDates>
        Accepted: Set<OperationId>
        NaturalKeys: Map<PartKind * string, Guid>
        Nonces: Set<string>
        Missing: Map<CompartmentOwner, EffectPosition list>
        Present: Map<CompartmentOwner, EffectPosition list>
    }

module internal Fold =
    let initial =
        {
            Head = LedgerPosition.genesis
            Families = Map.empty
            People = Map.empty
            Parts = Map.empty
            Origins = Map.empty
            Summaries = Map.empty
            ReportDates = Map.empty
            Accepted = Set.empty
            NaturalKeys = Map.empty
            Nonces = Set.empty
            Missing = Map.empty
            Present = Map.empty
        }

    let resolveFamily state =
        function
        | CaseSelector.Id id -> Map.tryFind id state.Families
        | CaseSelector.Reference text ->
            match CaseReference.parse text with
            | Error _ -> None
            | Ok reference ->
                Map.tryFind (PartKind.Case, CaseReference.identity reference) state.NaturalKeys
                |> Option.bind CaseId.create
                |> Option.bind (fun id -> Map.tryFind id state.Families)

    let resolveCase state selector =
        resolveFamily state selector |> Option.map _.Id

    let requester state principal =
        state.People
        |> Map.values
        |> Seq.tryFind (fun person -> person.Actor.Principal = principal)
        |> Option.map Person.access
        |> AccessView.ofActor

    let familyEnvironment state principal maintenance caseId =
        {
            FamilyEnvironment.Requester = requester state principal
            RequesterPrincipal = principal
            Maintenance = maintenance
            ReferenceOwner =
                fun reference ->
                    Map.tryFind (PartKind.Case, CaseReference.identity reference) state.NaturalKeys
                    |> Option.bind CaseId.create
            RelatedCase =
                fun selector ->
                    resolveFamily state selector
                    |> Option.filter (fun family ->
                        Access.can
                            principal
                            (requester state principal)
                            Capability.ReadCase
                            (Scope.Case family.Id))
                    |> Option.map (fun family -> family.Id, family.Case.Value.Lifecycle)
            AssigneeEligible =
                fun id ->
                    Map.tryFind id state.People
                    |> Option.exists (fun person ->
                        let access = Person.access person |> Some |> AccessView.ofActor

                        caseId
                        |> Option.exists (Access.eligibleAssignee person.Actor.Principal access))
        }

    let personEnvironment state principal maintenance targetPrincipal principalOwner =
        let cases =
            state.Families
            |> Map.values
            |> Seq.collect (fun family ->
                [
                    GrantTarget.CaseId family.Id, family.Id
                    GrantTarget.CaseReference(CaseReference.identity family.Reference), family.Id
                ])
            |> Map.ofSeq

        {
            PersonEnvironment.Requester = requester state principal
            RequesterPrincipal = principal
            Maintenance = maintenance
            TargetCases = cases
            EnabledHumanOwners =
                state.People
                |> Map.values
                |> Seq.filter (fun person ->
                    let access = Person.access person

                    access.Enabled
                    && PrincipalKey.isHuman access.Principal
                    && List.contains
                        {
                            ActorGrant.Role = Role.Owner
                            Scope = Scope.Installation
                        }
                        access.Grants)
                |> Seq.length
            ActorCount = state.People.Count
            TargetPrincipal = targetPrincipal
            PrincipalOwner = principalOwner
        }

    let private family state binding inputs =
        match Binding.caseTarget binding with
        | Some target -> resolveFamily state target
        | None ->
            InputSelection.target (InputTarget.Field BusinessField.CaseReference) inputs
            |> Option.bind (CaseSelector.Reference >> resolveFamily state)

    let private person state binding inputs =
        match binding with
        | Binding.People(id, _) -> Map.tryFind id state.People
        | Binding.PeopleNew _ ->
            InputSelection.principal inputs
            |> Option.bind (fun principal ->
                state.People
                |> Map.values
                |> Seq.tryFind (fun person -> person.Actor.Principal = principal))
        | _ -> None

    let private principalOwner state principal =
        Map.tryFind
            (PartKind.Person, (PrincipalKey.naturalKey principal).ToUpperInvariant())
            state.NaturalKeys
        |> Option.bind ActorId.create

    let snapshot state principal maintenance selected binding inputs =
        match selected with
        | Task.Family task ->
            let before = family state binding inputs

            DecisionSnapshot.family
                task
                (familyEnvironment state principal maintenance (before |> Option.map _.Id))
                (before |> Option.map DecisionFacts.fromReplay)
                (state.Parts
                 |> Map.values
                 |> Seq.filter (fun change ->
                     before
                     |> Option.exists (fun family ->
                         change.Aggregate = AggregateReference.Family family.Id))
                 |> Seq.toList)
                state.Origins
        | Task.Person task ->
            let before = person state binding inputs
            let current = before |> Option.map _.Actor.Principal

            let proposed =
                InputSelection.personPrincipal task.Declaration.Exposure current inputs

            DecisionSnapshot.person
                task
                (personEnvironment
                    state
                    principal
                    maintenance
                    (current |> Option.orElse proposed)
                    (proposed |> Option.bind (principalOwner state)))
                before

    let decide state context selected binding inputs =
        (snapshot
            state
            context.Decision.Principal
            context.Decision.Maintenance
            selected
            binding
            (Some inputs))
            .Decide
            context
            binding
            inputs

    let availability state principal maintenance selected binding inputs =
        (snapshot state principal maintenance selected binding inputs).Availability binding

    let describe state principal selected binding changes =
        (snapshot state principal false selected binding None).Describe changes

    let apply state changes =
        let summaries =
            ProjectionRows.summaries
                (state.Families |> Map.map (fun _ family -> family.Work |> Option.bind _.Value))
                state.Summaries
                changes

        changes
        |> List.fold
            (fun result (change: PartChange) ->
                result
                |> Result.bind (fun state ->
                    let applied =
                        match change.Aggregate with
                        | AggregateReference.Family id ->
                            CaseFamily.apply (Map.tryFind id state.Families) change
                            |> Result.map (fun family ->
                                { state with
                                    Families = Map.add id family state.Families
                                })
                        | AggregateReference.Person id ->
                            Person.apply (Map.tryFind id state.People) change
                            |> Result.map (fun person ->
                                { state with
                                    People = Map.add id person state.People
                                })

                    applied
                    |> Result.mapError FoldProblem.State
                    |> Result.bind (fun state ->
                        let key = change.Kind, change.PartId

                        let natural =
                            change.NaturalKey
                            |> Option.map (fun value -> change.Kind, value.ToUpperInvariant())

                        if
                            natural
                            |> Option.exists (fun key -> Map.containsKey key state.NaturalKeys)
                        then
                            Error FoldProblem.NaturalKey
                        else
                            let parts =
                                match change.Erases with
                                | None -> state.Parts
                                | Some owner ->
                                    state.Parts
                                    |> Map.map (fun _ part ->
                                        if PartChange.owner part = owner then
                                            { part with
                                                Value = PartValue.clear part.Value
                                            }
                                        else
                                            part)

                            Ok
                                { state with
                                    Parts = Map.add key change parts
                                    Origins =
                                        if Map.containsKey key state.Origins then
                                            state.Origins
                                        else
                                            Map.add key change.Stamp state.Origins
                                    NaturalKeys =
                                        natural
                                        |> Option.map (fun naturalKey ->
                                            Map.add naturalKey change.PartId state.NaturalKeys)
                                        |> Option.defaultValue state.NaturalKeys
                                })))
            (Ok state)
        |> Result.map (fun state -> { state with Summaries = summaries })

    let private executor state (record: RetainedOperation) selected =
        let declaration = Task.declaration selected
        let header = record.Root.Header

        match declaration.Exposure, declaration.Aggregate, header.Authority with
        | TaskExposure.Operator, AggregateKind.Person, Authority.Operator -> Ok None
        | TaskExposure.Client, _, Authority.Actor actor ->
            match Map.tryFind actor state.People with
            | None -> Error(FoldProblem.Executor ExecutorIssue.ActorNotEnabled)
            | Some person ->
                let principal = person.Actor.Principal
                let view = requester state principal

                let scope =
                    match record.Changes.Head.Aggregate with
                    | AggregateReference.Family id when
                        Binding.caseTarget record.Root.Structure.Binding |> Option.isSome
                        ->
                        Scope.Case id
                    | _ -> Scope.Installation

                let context =
                    {
                        DecisionContext.BusinessDate = header.BusinessDate
                        ObservedAt = header.RecordedAt
                        Principal = principal
                        Access = view
                        Maintenance = false
                        Lifetime = DecisionLifetime.Recorded
                    }

                let requiresOwner =
                    not declaration.Capabilities.IsEmpty
                    && declaration.Capabilities
                       |> List.forall (fun capability ->
                           Access.rolesFor capability = [ Role.Owner ])

                DecisionContext.executor context declaration.RequiresHumanRequester requiresOwner
                |> Result.mapError FoldProblem.Executor
                |> Result.bind (fun () ->
                    if not (Access.validActor principal view) then
                        Error(FoldProblem.Executor ExecutorIssue.ActorNotEnabled)
                    elif
                        declaration.Capabilities
                        |> List.exists (fun capability ->
                            Access.can principal view capability scope)
                    then
                        Ok(Some principal)
                    else
                        Error FoldProblem.Decision)
        | _ -> Error(FoldProblem.Executor ExecutorIssue.ActorNotEnabled)

    let private revisions state changes =
        changes
        |> List.fold
            (fun result (change: PartChange) ->
                result
                |> Result.bind (fun parts ->
                    let key = change.Kind, change.PartId

                    let prior =
                        Map.tryFind key parts
                        |> Option.map _.Revision
                        |> Option.defaultValue Revision.zero

                    if PartChange.successor prior change.Revision then
                        Ok(Map.add key change parts)
                    else
                        Error(FoldProblem.State RecordViolation.InvalidRevisionSequence)))
            (Ok state.Parts)
        |> Result.map ignore

    let private rejected =
        function
        | Rejection.InvalidState violation -> FoldProblem.State violation
        | Rejection.Refused(Refusal.Case CaseRefusal.CaseAlreadyExists) ->
            FoldProblem.State RecordViolation.InvalidHistoryStart
        | Rejection.Refused(Refusal.People PeopleRefusal.HumanPrincipalRequired) ->
            FoldProblem.TargetHumanRequired
        | _ -> FoldProblem.Decision

    let private equalChanges (record: RetainedOperation) (expected: PartChange list) =
        expected.Length = record.Changes.Length
        && List.forall2
            (fun (expected: PartChange) (accepted: PartChange) ->
                let cleared =
                    { expected with
                        Value = PartValue.clear expected.Value
                    }

                let publicAccepted =
                    { accepted with
                        Value = PartValue.clear accepted.Value
                    }

                cleared = publicAccepted
                && (let index = expected.Stamp.Position |> EffectPosition.index

                    match record.Root.Structure.Effects[index].Compartment with
                    | None -> expected.Value = accepted.Value
                    | Some ordinal when
                        record.Root.Structure.Compartments[ordinal]
                        |> Compartment.content
                        |> Option.isSome
                        ->
                        expected.Value = accepted.Value
                    | Some _ -> true))
            expected
            record.Changes

    /// Validates every accepted root, including executor and structural rules after private erasure.
    let append installation state (record: RetainedOperation) =
        let header = record.Root.Header

        if
            header.InstallationId <> installation
            || Set.contains header.OperationId state.Accepted
        then
            Error FoldProblem.Identity
        elif
            Sequence.value state.Head.Sequence >= Sequence.Maximum
            || header.Sequence <> Sequence.successor state.Head.Sequence
            || header.PreviousHash <> state.Head.EntryHash
        then
            Error FoldProblem.Link
        else
            match Registry.find record.Root.Structure.Task with
            | None -> Error FoldProblem.Task
            | Some selected ->
                executor state record selected
                |> Result.bind (fun principal ->
                    let principal =
                        principal
                        |> Option.orElseWith (fun () ->
                            InputSelection.personPrincipal
                                TaskExposure.Operator
                                None
                                (Some record.Root.Structure.Retained))

                    match principal with
                    | None -> Error(FoldProblem.Executor ExecutorIssue.ActorNotEnabled)
                    | Some principal ->
                        let context =
                            {
                                TaskContext.Decision =
                                    {
                                        BusinessDate = header.BusinessDate
                                        ObservedAt = header.RecordedAt
                                        Principal = principal
                                        Access = requester state principal
                                        Maintenance = false
                                        Lifetime = DecisionLifetime.Recorded
                                    }
                                Stamp =
                                    {
                                        OperationId = header.OperationId
                                        Authority = header.Authority
                                        RecordedAt = header.RecordedAt
                                        Position =
                                            EffectPosition.create header.Sequence 0 |> Option.get
                                    }
                            }

                        let current =
                            snapshot
                                state
                                principal
                                false
                                selected
                                record.Root.Structure.Binding
                                record.Inputs

                        let decision =
                            revisions state record.Changes
                            |> Result.bind (fun () ->
                                match record.Inputs with
                                | Some inputs when
                                    current.FactsAvailable record.Root.Structure.Binding
                                    ->
                                    current.Decide context record.Root.Structure.Binding inputs
                                | _ ->
                                    current.DecideRetained
                                        context
                                        record.Root.Structure.Binding
                                        record.Root.Structure.Retained
                                |> Result.mapError rejected
                                |> Result.bind (fun expected ->
                                    if equalChanges record expected then
                                        Ok()
                                    else
                                        Error FoldProblem.Decision))

                        decision
                        |> Result.bind (fun () ->
                            let nonces =
                                record.Root.Structure.Compartments
                                |> List.choose (
                                    Compartment.content >> Option.map (fst >> Convert.ToHexString)
                                )

                            if
                                List.distinct nonces <> nonces
                                || List.exists
                                    (fun nonce -> Set.contains nonce state.Nonces)
                                    nonces
                            then
                                Error FoldProblem.Nonce
                            else
                                apply state record.Changes
                                |> Result.bind (fun next ->
                                    let missing, present =
                                        record.Root.Structure.Compartments
                                        |> List.fold
                                            (fun (missing, present) compartment ->
                                                let position =
                                                    EffectPosition.create header.Sequence 0
                                                    |> Option.get

                                                let add map =
                                                    Map.change
                                                        compartment.Descriptor.Owner
                                                        (fun previous ->
                                                            Some(
                                                                position
                                                                :: Option.defaultValue
                                                                    []
                                                                    previous
                                                            ))
                                                        map

                                                if
                                                    Compartment.content compartment
                                                    |> Option.isSome
                                                then
                                                    missing, add present
                                                else
                                                    add missing, present)
                                            (state.Missing, state.Present)

                                    let proof =
                                        record.Changes
                                        |> List.fold
                                            (fun result change ->
                                                result
                                                |> Result.bind (fun (missing, present) ->
                                                    match change.Erases with
                                                    | None -> Ok(missing, present)
                                                    | Some owner ->
                                                        let precedes position =
                                                            position < change.Stamp.Position

                                                        if
                                                            Map.tryFind owner present
                                                            |> Option.defaultValue []
                                                            |> List.exists precedes
                                                        then
                                                            Error FoldProblem.ErasureIncomplete
                                                        else
                                                            let unresolved =
                                                                Map.tryFind owner missing
                                                                |> Option.defaultValue []
                                                                |> List.filter (
                                                                    precedes >> not
                                                                )

                                                            Ok(
                                                                Map.add
                                                                    owner
                                                                    unresolved
                                                                    missing,
                                                                present
                                                            )))
                                            (Ok(missing, present))

                                    proof
                                    |> Result.map (fun (missing, present) ->
                                        let dates =
                                            match record.Changes.Head.Aggregate with
                                            | AggregateReference.Person _ -> next.ReportDates
                                            | AggregateReference.Family id ->
                                                let status =
                                                    next.Families[id]
                                                    |> CaseFamily.snapshot
                                                    |> Option.bind (fun snapshot -> CaseState.fields snapshot.State)
                                                    |> Option.map _.Status

                                                let date = CalendarDate.ofDate header.BusinessDate.Value
                                                let previous = Map.tryFind id next.ReportDates
                                                Map.add id (CaseReportDates.record date status previous) next.ReportDates

                                        { next with
                                            ReportDates = dates
                                            Head = record.Root.Position
                                            Accepted = Set.add header.OperationId next.Accepted
                                            Nonces = Set.union state.Nonces (Set.ofList nonces)
                                            Missing = missing
                                            Present = present
                                        }))))

    let complete state =
        if state.Missing |> Map.exists (fun _ positions -> not positions.IsEmpty) then
            Error FoldProblem.Erasure
        else
            Ok state
