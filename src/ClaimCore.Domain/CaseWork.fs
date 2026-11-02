namespace ClaimCore.Domain

open System

/// Omission preserves a work fact; setting and clearing are explicit independent choices.
[<RequireQualifiedAccess>]
type WorkValue<'value> =
    | Keep
    | Set of 'value
    | Clear

type CaseWorkUpdate<'actor, 'date> =
    {
        Responsible: WorkValue<'actor>
        FollowUp: WorkValue<'date>
    }

type CaseWorkInput = CaseWorkUpdate<string, string>
type ValidCaseWorkUpdate = CaseWorkUpdate<ActorId, CalendarDate>

/// Work is erasable case state. Labels and current availability never become retained facts.
type CaseWork =
    {
        Responsible: ActorId option
        FollowUp: CalendarDate option
    }

module WorkValue =
    let traverse parse =
        function
        | WorkValue.Keep -> Ok WorkValue.Keep
        | WorkValue.Clear -> Ok WorkValue.Clear
        | WorkValue.Set value -> parse value |> Result.map WorkValue.Set

    let map transform value =
        traverse (transform >> Ok) value
        |> Result.defaultWith (fun () -> invalidOp "A total work-value mapping cannot fail.")

    let apply current =
        function
        | WorkValue.Keep -> current
        | WorkValue.Clear -> None
        | WorkValue.Set value -> Some value

module CaseWork =
    let empty = { Responsible = None; FollowUp = None }

    let apply (update: ValidCaseWorkUpdate) current =
        {
            Responsible = WorkValue.apply current.Responsible update.Responsible
            FollowUp = WorkValue.apply current.FollowUp update.FollowUp
        }

    let authored (update: ValidCaseWorkUpdate) : CaseWorkInput =
        {
            Responsible = WorkValue.map ActorId.format update.Responsible
            FollowUp = WorkValue.map CalendarDate.format update.FollowUp
        }

module CaseWorkInput =
    let private actor raw =
        match Guid.TryParseExact(raw, "D") with
        | true, value ->
            ActorId.create value
            |> Option.map Ok
            |> Option.defaultValue (Error Violation.ActorIdRequired)
        | _ -> Error Violation.ActorIdRequired

    let parse (input: CaseWorkInput) =
        let responsible = WorkValue.traverse actor input.Responsible
        // Planned dates share permanent calendar syntax, without event-date admission's maximum.
        let followUp = WorkValue.traverse CalendarDate.parse input.FollowUp

        match responsible, followUp with
        | Ok WorkValue.Keep, Ok WorkValue.Keep ->
            Error [ BusinessField.FollowUpDate, Violation.UpdateRequired ]
        | Ok responsible, Ok followUp ->
            Ok
                {
                    ValidCaseWorkUpdate.Responsible = responsible
                    FollowUp = followUp
                }
        | _ ->
            [
                BusinessField.ResponsibleActor, responsible |> Result.map ignore
                BusinessField.FollowUpDate, followUp |> Result.map ignore
            ]
            |> List.choose (fun (field, result) ->
                match result with
                | Ok() -> None
                | Error issue -> Some(field, issue))
            |> Error
