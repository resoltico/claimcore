namespace ClaimCore.Domain.Changes

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates

module PartDescriptions =
    let private collect results =
        results
        |> List.fold
            (fun current result ->
                current
                |> Result.bind (fun values -> result |> Result.map (fun value -> value :: values)))
            (Ok [])
        |> Result.map List.rev

    let private described (change: PartChange) description =
        {
            DescribedPart.PartId = change.PartId
            Revision = change.Revision
            Description = description
        }

    let family
        (before: CaseFamily option)
        (notes: Map<NoteId, NoteValue>)
        (changes: PartChange list)
        =
        changes
        |> List.map (fun change ->
            let prior = before

            let owner =
                match change.Aggregate with
                | AggregateReference.Family id -> Some id
                | _ -> None

            if owner.IsNone || (prior |> Option.exists (fun state -> Some state.Id <> owner)) then
                Error RecordViolation.InvalidSubjectState
            else
                let description =
                    match change.Value with
                    | PartValue.Case value ->
                        let reference =
                            prior |> Option.map _.Reference |> Option.orElse value.Reference

                        match reference with
                        | None -> Error RecordViolation.InvalidHistoryStart
                        | Some reference ->
                            let before =
                                prior
                                |> Option.map (fun state ->
                                    {
                                        CaseValue.Reference = Some reference
                                        Lifecycle = state.Case.Value.Lifecycle
                                        Status = state.Case.Value.Status
                                        Content = state.Case.Value.Content
                                        Disposition = None
                                    })

                            Ok(
                                PartDescription.Case(
                                    CaseDescription.describe reference before value
                                )
                            )
                    | PartValue.Payment value ->
                        let before =
                            OperationId.create change.PartId
                            |> Option.bind (fun id ->
                                prior |> Option.bind (fun state -> Map.tryFind id state.Payments))
                            |> Option.map _.Value

                        Ok(PartDescription.Payment(PaymentDescription.describe before value))
                    | PartValue.Work value ->
                        let before = prior |> Option.bind _.Work |> Option.bind _.Value
                        Ok(PartDescription.Work(WorkDescription.describe before value))
                    | PartValue.Reminder value ->
                        let before =
                            ReminderId.create change.PartId
                            |> Option.bind (fun id ->
                                prior |> Option.bind (fun state -> Map.tryFind id state.Reminders))
                            |> Option.map _.Value

                        Ok(PartDescription.Reminder(ReminderDescription.describe before value))
                    | PartValue.Note value ->
                        match NoteId.create change.PartId with
                        | None -> Error RecordViolation.InvalidSubjectState
                        | Some id ->
                            let before = Map.tryFind id notes

                            if
                                before.IsNone
                                && (prior
                                    |> Option.exists (fun state -> Map.containsKey id state.Notes))
                            then
                                Error RecordViolation.InvalidSubjectState
                            else
                                Ok(PartDescription.Note(NoteDescription.describe before value))
                    | PartValue.Person _ -> Error RecordViolation.InvalidSubjectState

                description |> Result.map (described change))
        |> collect

    let person (before: Person option) (changes: PartChange list) =
        changes
        |> List.map (fun change ->
            match change.Aggregate, change.Value with
            | AggregateReference.Person id, PartValue.Person value when
                before |> Option.forall (fun state -> state.Id = id)
                ->
                PersonDescription.describe (before |> Option.map Person.access) value
                |> PartDescription.Person
                |> described change
                |> Ok
            | _ -> Error RecordViolation.InvalidSubjectState)
        |> collect
