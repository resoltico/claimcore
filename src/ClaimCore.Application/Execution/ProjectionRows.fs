namespace ClaimCore.Application.Execution

open System
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes

/// Projection attribution is derived from changes; it is not additional decision state.
type internal Attribution =
    {
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
    }

type internal WorkProjection =
    {
        Snapshot: CaseWorkSnapshot option
        Revision: Revision
        Changed: Attribution option
        SummaryChanged: Attribution option
    }

type internal PaymentProjection =
    {
        Id: OperationId
        Origin: SubjectStamp
        Value: PaymentValue
    }

type internal ReminderProjection =
    {
        Id: ReminderId
        Revision: Revision
        Value: ReminderValue
        Changed: Attribution
    }

type internal NoteProjection =
    {
        Id: NoteId
        Origin: SubjectStamp
        Value: NoteValue
    }

type internal FamilyProjection =
    {
        Snapshot: CaseSnapshot
        Index: CaseIndex
        Work: WorkProjection
        Payments: PaymentProjection list
        Reminders: ReminderProjection list
        Notes: NoteProjection list
    }

type internal PersonProjection =
    {
        Actor: Actor
        Revision: Revision
        Grants: Map<ActorGrant, EffectPosition>
        ProfileErasure: EffectPosition option
        DisplayNameSearch: string option
        ContactSearch: string option
    }

/// The same row construction serves a live accepted change and the verified history fold.
module internal ProjectionRows =
    let attribution (stamp: SubjectStamp) =
        {
            RecordedAt = stamp.RecordedAt
            RecordedBy = stamp.ActorId
        }

    let summaries
        (before: Map<CaseId, CaseWork option>)
        (current: Map<CaseId, Attribution>)
        (changes: PartChange list)
        =
        changes
        |> List.fold
            (fun current change ->
                match change.Aggregate, change.Value with
                | AggregateReference.Family id, PartValue.Work value ->
                    match value |> Option.bind _.Summary with
                    | None -> Map.remove id current
                    | Some summary ->
                        let previous =
                            Map.tryFind id before |> Option.flatten |> Option.bind _.Summary

                        if previous = Some summary then
                            current
                        else
                            Map.add id (attribution change.Stamp) current
                | _ -> current)
            current
        |> fun current ->
            changes
            |> List.fold
                (fun current change ->
                    match change.Erases with
                    | Some(CompartmentOwner.Family id) -> Map.remove id current
                    | _ -> current)
                current

    let family (state: CaseFamily) (parts: PartChange list) origins summary =
        CaseFamily.rows state parts
        |> Result.bind (fun (_, index) ->
            match CaseFamily.snapshot state with
            | None -> Error RecordViolation.InvalidSubjectState
            | Some snapshot ->
                let erased = state.Case.Value.Lifecycle = Lifecycle.Erased
                let payments = ResizeArray<PaymentProjection>()
                let reminders = ResizeArray<ReminderProjection>()
                let notes = ResizeArray<NoteProjection>()

                for change in parts do
                    let value =
                        if erased then
                            PartValue.clear change.Value
                        else
                            change.Value

                    match value with
                    | PartValue.Payment value ->
                        payments.Add
                            {
                                Id = OperationId.create change.PartId |> Option.get
                                Origin = Map.find (change.Kind, change.PartId) origins
                                Value = value
                            }
                    | PartValue.Reminder value ->
                        reminders.Add
                            {
                                Id = ReminderId.create change.PartId |> Option.get
                                Revision = change.Revision
                                Value = value
                                Changed = attribution change.Stamp
                            }
                    | PartValue.Note value ->
                        notes.Add
                            {
                                Id = NoteId.create change.PartId |> Option.get
                                Origin = Map.find (change.Kind, change.PartId) origins
                                Value = value
                            }
                    | _ -> ()

                let work =
                    match state.Work with
                    | None ->
                        {
                            Snapshot =
                                if erased then
                                    None
                                else
                                    Some
                                        {
                                            CaseId = state.Id
                                            Revision = Revision.zero
                                            Work = CaseWork.empty
                                        }
                            Revision = Revision.zero
                            Changed = None
                            SummaryChanged = None
                        }
                    | Some work ->
                        {
                            Snapshot =
                                if erased then
                                    None
                                else
                                    work.Value
                                    |> Option.map (fun value ->
                                        {
                                            CaseId = state.Id
                                            Revision = work.Revision
                                            Work = value
                                        })
                            Revision = work.Revision
                            Changed = Some(attribution work.Stamp)
                            SummaryChanged = if erased then None else summary
                        }

                if
                    work.Snapshot |> Option.bind (fun value -> value.Work.Summary) |> Option.isSome
                    <> work.SummaryChanged.IsSome
                then
                    Error RecordViolation.InvalidSubjectState
                else
                    Ok
                        {
                            Snapshot = snapshot
                            Index = index
                            Work = work
                            Payments = List.ofSeq payments
                            Reminders = List.ofSeq reminders
                            Notes = List.ofSeq notes
                        })

    let person state =
        Person.rows state
        |> Result.map (fun (actor, revision, grants, erasure, name, contact) ->
            {
                Actor = actor
                Revision = revision
                Grants = grants
                ProfileErasure = erasure
                DisplayNameSearch = name
                ContactSearch = contact
            })
