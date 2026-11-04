namespace ClaimCore.Domain

open System

/// Omission preserves a work fact; setting and clearing are explicit independent choices.
[<RequireQualifiedAccess>]
type WorkValue<'value> =
    | Keep
    | Set of 'value
    | Clear

type CaseWorkUpdate<'actor, 'summary> =
    {
        Responsible: WorkValue<'actor>
        Summary: WorkValue<'summary>
    }

type CaseWorkInput = CaseWorkUpdate<string, string>
type ValidCaseWorkUpdate = CaseWorkUpdate<ActorId, HandlingSummary>

/// Work is erasable case state. Labels and current availability never become retained facts.
type CaseWork =
    {
        Responsible: ActorId option
        Summary: HandlingSummary option
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
    let empty = { Responsible = None; Summary = None }

    let apply (update: ValidCaseWorkUpdate) current =
        {
            Responsible = WorkValue.apply current.Responsible update.Responsible
            Summary = WorkValue.apply current.Summary update.Summary
        }

    let authored (update: ValidCaseWorkUpdate) : CaseWorkInput =
        {
            Responsible = WorkValue.map ActorId.format update.Responsible
            Summary = WorkValue.map HandlingSummary.format update.Summary
        }

module CaseWorkInput =
    let private actor raw =
        match Guid.TryParseExact(raw, "D") with
        | true, value ->
            ActorId.create value
            |> Option.map Ok
            |> Option.defaultValue (Error Violation.ActorIdRequired)
        | _ -> Error Violation.ActorIdRequired

    let internal parseWith admit (input: CaseWorkInput) =
        let responsible = WorkValue.traverse actor input.Responsible

        let summary =
            WorkValue.traverse (HandlingSummary.parse >> Result.bind admit) input.Summary

        match responsible, summary with
        | Ok WorkValue.Keep, Ok WorkValue.Keep ->
            Error [ BusinessField.HandlingSummary, Violation.UpdateRequired ]
        | Ok responsible, Ok summary ->
            Ok
                {
                    ValidCaseWorkUpdate.Responsible = responsible
                    Summary = summary
                }
        | _ ->
            [
                BusinessField.ResponsibleActor, responsible |> Result.map ignore
                BusinessField.HandlingSummary, summary |> Result.map ignore
            ]
            |> List.choose (fun (field, result) ->
                match result with
                | Ok() -> None
                | Error issue -> Some(field, issue))
            |> Error

    let parse input = parseWith Ok input
    let admit input = parseWith HandlingSummary.admit input

type CaseWorkSnapshot =
    {
        CaseId: CaseId
        Revision: Revision
        Work: CaseWork
    }

[<RequireQualifiedAccess>]
type WorkActKind = | UpdateWork

module WorkActKind =
    let vocabulary =
        Vocabulary.ofUnion<WorkActKind> "workAct" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type WorkAct<'value> = UpdateWork of 'value

type AuthoredWorkAct = WorkAct<CaseWorkInput>
