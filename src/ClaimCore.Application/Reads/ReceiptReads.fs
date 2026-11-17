namespace ClaimCore.Application.Reads

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application.Execution
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open ClaimCore.Domain.Tasks

/// Receipt disclosure uses the action's admitted rights; lookups supply freshly loaded rights.
module internal ReceiptReads =
    let private fault = Error CoreFault.StoreIntegrityError

    let private actor tx id =
        task {
            let! person = AggregateStorage.person tx id
            return person |> Option.map (fun person -> Actor.currentReference person.Actor)
        }

    let private responsible tx caseId id =
        task {
            let! person = AggregateStorage.person tx id

            return
                person
                |> Option.map (fun person ->
                    let view = Person.access person |> Some |> AccessView.ofActor

                    let availability =
                        if not person.Actor.Enabled then
                            ActorAvailability.Disabled
                        elif Access.eligibleAssignee person.Actor.Principal view caseId then
                            ActorAvailability.Available
                        else
                            ActorAvailability.NotEligible

                    Actor.reference availability person.Actor)
        }

    let private fields
        (declaration: TaskDeclaration)
        (change: PartChange)
        changes
        (inputs: InputMap option)
        (description: CasePartDescription)
        =
        let authored =
            inputs
            |> Option.map (fun inputs ->
                inputs.Values
                |> Map.toList
                |> List.collect (function
                    | TaskInputId.Target(InputTarget.Field field), _ when
                        (BusinessField.describe field).Group.IsSome
                        ->
                        [ field ]
                    | TaskInputId.Target(InputTarget.Group group), InputValue.Text "CLEAR" ->
                        BusinessField.ofGroup group
                    | _ -> []))
            |> Option.defaultValue []

        if change.Revision = Revision.first then
            declaration.Inputs
            |> List.choose (fun input ->
                match input.Source, input.Input with
                | InputSource.Authored, TaskInputId.Target(InputTarget.Field field) when
                    (BusinessField.describe field).Group.IsSome
                    ->
                    Some field
                | _ -> None)
        elif changes |> List.exists (PartChange.paymentDifference >> Option.isSome) then
            BusinessField.ofGroup RecordGroup.Payment
        elif not authored.IsEmpty then
            List.distinct authored
        else
            let changed =
                (description.Fields |> List.map _.FieldName)
                @ (description.Money |> List.map _.FieldName)

            changed
            |> List.filter (fun field ->
                (BusinessField.describe field).Group = Some RecordGroup.Decision)
            |> List.collect (fun field ->
                field :: ((BusinessField.describe field).Pairs |> Option.toList))
            |> List.distinct

    let read basis principal access (tx: ReadTx) (record: RetainedOperation) =
        task {
            let root = record.Root
            let! author = actor tx root.Header.ActorId

            match author, Registry.find root.Structure.Task with
            | Some author, Some selected ->
                let declaration = ClaimCore.Domain.Tasks.Task.declaration selected

                let! subject =
                    task {
                        match record.Changes.Head.Aggregate with
                        | AggregateReference.Person id when declaration.Target = ActSubject.People ->
                            let! target = actor tx id

                            match target with
                            | None -> return fault
                            | Some target ->
                                return
                                    Ok(
                                        ReceiptSubject.People
                                            {
                                                TargetActor =
                                                    if
                                                        Access.can
                                                            principal
                                                            access
                                                            Capability.ReadAccess
                                                            Scope.Installation
                                                    then
                                                        PeopleTargetDisclosure.Disclosed target
                                                    else
                                                        PeopleTargetDisclosure.Withheld id
                                                Act = declaration.Id
                                                Effects =
                                                    record.Changes
                                                    |> List.map (fun change ->
                                                        {
                                                            PeopleReceiptEffect.Act =
                                                                declaration.Id
                                                            Id =
                                                                EffectId.create
                                                                    root.Header.OperationId
                                                                    (EffectPosition.index
                                                                        change.Stamp.Position)
                                                                |> Option.get
                                                            Position = change.Stamp.Position
                                                        })
                                            }
                                    )
                        | AggregateReference.Family id ->
                            let! projected = FamilyReads.findCase tx id
                            let! current = AggregateStorage.familyForReceipt tx id record.Changes

                            match projected, current with
                            | Some projected, Some current when
                                current.Snapshot = projected.Snapshot
                                ->
                                let erased = current.Snapshot.State = CaseState.Erased

                                let available =
                                    root.Structure.Compartments
                                    |> List.exists (Compartment.content >> Option.isSome)

                                if erased && available then
                                    return fault
                                else
                                    let canRead revision currentRevision =
                                        Access.can
                                            principal
                                            access
                                            Capability.ReadHistoryFull
                                            (Scope.Case id)
                                        || (revision = currentRevision
                                            && Access.can
                                                principal
                                                access
                                                Capability.ReadCase
                                                (Scope.Case id))

                                    match declaration.Target with
                                    | ActSubject.Case ->
                                        let accepted =
                                            record.Changes
                                            |> List.tryPick (fun change ->
                                                PartChange.caseSnapshot
                                                    current.Snapshot.Reference
                                                    change
                                                |> Option.map (fun snapshot -> change, snapshot))

                                        if erased then
                                            let revision =
                                                record.Changes
                                                |> List.filter (fun change ->
                                                    change.PartId = CaseId.value id)
                                                |> List.tryLast
                                                |> Option.map _.Revision

                                            match revision with
                                            | None -> return fault
                                            | Some revision ->
                                                return
                                                    Ok(
                                                        ReceiptSubject.Case
                                                            {
                                                                CaseId = id
                                                                Reference =
                                                                    current.Snapshot.Reference
                                                                Revision = revision
                                                                Act = declaration.Id
                                                                Snapshot = SnapshotDisclosure.Erased
                                                            }
                                                    )
                                        else
                                            match accepted with
                                            | None -> return fault
                                            | Some(change, snapshot) ->
                                                let! shown =
                                                    task {
                                                        if
                                                            not (
                                                                canRead
                                                                    snapshot.Revision
                                                                    current.Snapshot.Revision
                                                            )
                                                        then
                                                            return Ok SnapshotDisclosure.Withheld
                                                        else
                                                            let! before =
                                                                OperationStorage.previous
                                                                    tx.Connection
                                                                    tx.InstallationId
                                                                    change
                                                                    tx.CancellationToken

                                                            match
                                                                LiveCase.ofState snapshot.State,
                                                                CaseDescription.fromChanges
                                                                    current.Snapshot.Reference
                                                                    before
                                                                    change
                                                            with
                                                            | Some live, Ok description ->
                                                                return
                                                                    Ok(
                                                                        SnapshotDisclosure.Disclosed
                                                                            {
                                                                                State = live
                                                                                Disposition =
                                                                                    description
                                                                                        .Disposition
                                                                                RecordedFields =
                                                                                    fields
                                                                                        declaration
                                                                                        change
                                                                                        record
                                                                                            .Changes
                                                                                        record
                                                                                            .Inputs
                                                                                        description
                                                                                Payments =
                                                                                    record.Changes
                                                                                    |> List.choose
                                                                                        PartChange
                                                                                            .paymentDifference
                                                                            }
                                                                    )
                                                            | _ -> return fault
                                                    }

                                                return
                                                    shown
                                                    |> Result.map (fun shown ->
                                                        ReceiptSubject.Case
                                                            {
                                                                CaseId = id
                                                                Reference =
                                                                    current.Snapshot.Reference
                                                                Revision = snapshot.Revision
                                                                Act = declaration.Id
                                                                Snapshot = shown
                                                            })
                                    | ActSubject.Work ->
                                        let change = record.Changes |> List.tryExactlyOne

                                        match change with
                                        | None -> return fault
                                        | Some change ->
                                            let currentRevision =
                                                current.Revisions
                                                |> Map.tryFind (PartKind.Work, CaseId.value id)
                                                |> Option.defaultValue Revision.zero

                                            let! shown =
                                                task {
                                                    if erased then
                                                        return Ok WorkDisclosure.Erased
                                                    elif
                                                        not (
                                                            canRead change.Revision currentRevision
                                                        )
                                                    then
                                                        return Ok WorkDisclosure.Withheld
                                                    else
                                                        match PartChange.workSnapshot change with
                                                        | None -> return fault
                                                        | Some snapshot ->
                                                            let! responsible =
                                                                task {
                                                                    match
                                                                        snapshot.Work.Responsible
                                                                    with
                                                                    | None -> return None
                                                                    | Some responsibleId ->
                                                                        return!
                                                                            responsible
                                                                                tx
                                                                                id
                                                                                responsibleId
                                                                }

                                                            if
                                                                snapshot.Work.Responsible.IsSome
                                                                <> responsible.IsSome
                                                            then
                                                                return fault
                                                            else
                                                                return
                                                                    Ok(
                                                                        WorkDisclosure.Disclosed
                                                                            {
                                                                                CaseId = id
                                                                                Revision =
                                                                                    change.Revision
                                                                                Responsible =
                                                                                    responsible
                                                                                Summary =
                                                                                    snapshot.Work
                                                                                        .Summary
                                                                            }
                                                                    )
                                                }

                                            return
                                                shown
                                                |> Result.map (fun shown ->
                                                    ReceiptSubject.Work
                                                        {
                                                            CaseId = id
                                                            Reference = current.Snapshot.Reference
                                                            Revision = change.Revision
                                                            Act = declaration.Id
                                                            Snapshot = shown
                                                        })
                                    | ActSubject.Reminder ->
                                        let versions =
                                            record.Changes
                                            |> List.map (fun change ->
                                                match ReminderId.create change.PartId with
                                                | None -> fault
                                                | Some reminder ->
                                                    let revision =
                                                        current.Revisions
                                                        |> Map.tryFind (
                                                            PartKind.Reminder,
                                                            ReminderId.value reminder
                                                        )

                                                    let shown =
                                                        if erased then
                                                            Ok ReminderDisclosure.Erased
                                                        elif
                                                            revision
                                                            |> Option.exists (
                                                                canRead change.Revision
                                                            )
                                                            |> not
                                                        then
                                                            Ok ReminderDisclosure.Withheld
                                                        else
                                                            PartChange.reminderSnapshot change
                                                            |> Option.map (
                                                                ReminderDisclosure.Disclosed >> Ok
                                                            )
                                                            |> Option.defaultValue fault

                                                    shown
                                                    |> Result.map (fun shown ->
                                                        {
                                                            ReminderVersion.ReminderId = reminder
                                                            Revision = change.Revision
                                                            Snapshot = shown
                                                        }))

                                        match QueryResults.collect versions with
                                        | Ok [ version ] ->
                                            return
                                                Ok(
                                                    ReceiptSubject.Reminder
                                                        {
                                                            CaseId = id
                                                            Reference = current.Snapshot.Reference
                                                            Act = declaration.Id
                                                            Effects =
                                                                ReminderReceiptEffects.Single
                                                                    version
                                                        }
                                                )
                                        | Ok [ completed; next ] ->
                                            return
                                                Ok(
                                                    ReceiptSubject.Reminder
                                                        {
                                                            CaseId = id
                                                            Reference = current.Snapshot.Reference
                                                            Act = declaration.Id
                                                            Effects =
                                                                ReminderReceiptEffects
                                                                    .CompletedAndPlanned(
                                                                        completed,
                                                                        next
                                                                    )
                                                        }
                                                )
                                        | _ -> return fault
                                    | ActSubject.Note ->
                                        match record.Changes |> List.tryExactlyOne with
                                        | None -> return fault
                                        | Some change ->
                                            match NoteId.create change.PartId with
                                            | None -> return fault
                                            | Some note ->
                                                let currentRevision =
                                                    current.Revisions
                                                    |> Map.tryFind (
                                                        PartKind.Note,
                                                        NoteId.value note
                                                    )

                                                let shown =
                                                    if erased then
                                                        Ok NoteDisclosure.Erased
                                                    elif
                                                        currentRevision
                                                        |> Option.exists (canRead change.Revision)
                                                        |> not
                                                    then
                                                        Ok NoteDisclosure.Withheld
                                                    else
                                                        PartChange.noteSnapshot change
                                                        |> Option.map (
                                                            NoteDisclosure.Disclosed >> Ok
                                                        )
                                                        |> Option.defaultValue fault

                                                return
                                                    shown
                                                    |> Result.map (fun shown ->
                                                        ReceiptSubject.Note
                                                            {
                                                                CaseId = id
                                                                Reference =
                                                                    current.Snapshot.Reference
                                                                NoteId = note
                                                                Act = declaration.Id
                                                                Snapshot = shown
                                                            })
                                    | ActSubject.People -> return fault
                            | _ -> return fault
                        | _ -> return fault
                    }

                return
                    subject
                    |> Result.map (fun subject ->
                        {
                            OperationReceipt.OperationId = root.Header.OperationId
                            Position = root.Position
                            Subject = subject
                            RecordedAt = root.Header.RecordedAt
                            RecordedBy = author
                            Basis = basis
                        })
            | _ -> return fault
        }
