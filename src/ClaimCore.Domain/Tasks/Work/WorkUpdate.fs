module ClaimCore.Domain.Tasks.Work.Update

open System
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    let values =
        [ field BusinessField.ResponsibleActor; field BusinessField.HandlingSummary ]
        |> List.map (fun input ->
            { optional input with
                Mode = TaskInputMode.Update
                Clearable = true
            })

    family
        "work.update"
        ActSubject.Work
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.ManageCaseWork ]
        (operation
         :: caseTarget
         :: required (target InputTarget.ExpectedWorkRevision)
         :: values)

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Update: ValidCaseWorkUpdate }

let private actor value =
    match Guid.TryParseExact(value, "D") with
    | true, value ->
        ActorId.create value
        |> Option.map Ok
        |> Option.defaultValue (Error Violation.ActorIdRequired)
    | _ -> Error Violation.ActorIdRequired

let private parse inputs =
    Admission.map2
        (fun responsible summary ->
            {
                Update =
                    {
                        Responsible = responsible
                        Summary = summary
                    }
            })
        (Parsing.workValue (InputTarget.Field BusinessField.ResponsibleActor) actor inputs)
        (Parsing.workValue
            (InputTarget.Field BusinessField.HandlingSummary)
            HandlingSummary.parse
            inputs)
    |> Result.bind (fun command ->
        if
            command.Update.Responsible = WorkValue.Keep
            && command.Update.Summary = WorkValue.Keep
        then
            Error(
                Refusal.invalidInput (
                    InputTarget.Field BusinessField.HandlingSummary,
                    Violation.UpdateRequired
                )
            )
        else
            Ok command)

let check inputs = Parsing.check parse inputs

let availability environment state binding =
    let offered =
        FamilyRules.availability
            declaration
            environment
            state
            binding
            capability
            (FamilyRules.active false)

    let offered =
        match state |> Option.bind _.Work |> Option.bind _.Value with
        | None -> offered
        | Some work ->
            { offered with
                Seeds =
                    Map.ofList
                        [
                            field BusinessField.ResponsibleActor,
                            work.Responsible
                            |> Option.map (ActorId.format >> InputValue.Text)
                            |> Option.defaultValue InputValue.Null
                            field BusinessField.HandlingSummary,
                            work.Summary
                            |> Option.map (HandlingSummary.format >> InputValue.Text)
                            |> Option.defaultValue InputValue.Null
                        ]
            }

    match state with
    | None -> offered
    | Some state when state.Case.Value.Status = CaseStatus.Closed ->
        { offered with
            Editable = offered.Editable |> List.filter ((<>) (field BusinessField.ResponsibleActor))
        }
    | Some _ -> offered

let decide context environment (state: CaseFamily option) binding inputs =
    match state, binding with
    | Some state, Binding.Work(_, expected) ->
        FamilyRules.admit context environment state binding capability (FamilyRules.active false)
        |> Result.bind (fun () ->
            let revision =
                state.Work |> Option.map _.Revision |> Option.defaultValue Revision.zero

            FamilyRules.revision expected revision
            |> Result.bind (fun () -> parse inputs)
            |> Result.bind (fun command ->
                let before =
                    match state.Work with
                    | None -> Some CaseWork.empty
                    | Some part -> part.Value

                match before with
                | None -> Error FamilyRules.unavailable
                | Some before ->
                    let after = CaseWork.apply command.Update before

                    if
                        after.Responsible <> before.Responsible
                        && state.Case.Value.Status <> CaseStatus.Opened
                    then
                        Error(FamilyRules.notOffered state)
                    elif
                        (match command.Update.Responsible with
                         | WorkValue.Set id -> not (environment.AssigneeEligible id)
                         | _ -> false)
                    then
                        Error(Refusal.Case CaseRefusal.AssigneeUnavailable)
                    elif after = before then
                        Error(Refusal.Case CaseRefusal.WorkNoChanges)
                    else
                        let admission =
                            match command.Update.Summary, context.Decision.Lifetime with
                            | WorkValue.Set summary, lifetime when
                                lifetime <> DecisionLifetime.Recorded
                                && Some summary <> before.Summary
                                ->
                                HandlingSummary.admit summary
                                |> Result.map ignore
                                |> Parsing.at (InputTarget.Field BusinessField.HandlingSummary)
                            | _ -> Ok()

                        admission
                        |> Result.map (fun () ->
                            [
                                FamilyRules.change
                                    context
                                    state.Id
                                    (CaseId.value state.Id)
                                    (Revision.successor revision)
                                    (PartValue.Work(Some after))
                            ])))
        |> Parsing.reject
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decisionFactsAvailable (state: CaseFamily option) (_: Binding) =
    state |> Option.bind _.Work |> Option.forall (fun part -> part.Value.IsSome)

let task =
    Task.Family
        {
            Declaration = declaration
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = decisionFactsAvailable
            Describe = PartDescriptions.family
        }
