module ClaimCore.TestSupport.Submissions

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

// Construction helpers supply authored values only; expectations remain literal in the tests.
let createRaw task operation binding values =
    OperationSubmission.admit
        {
            SubmissionEnvelope.OperationId = operation
            Task = TaskId.ofLiteral task
            Binding = binding
            Inputs = values
        }
    |> Result.defaultWith (fun () -> failwith "The fixture requires a declared submission shape.")

let create task operation binding values =
    createRaw task operation (OperationSubmission.rawBinding binding) values

let field name =
    TaskInputId.Target(InputTarget.Field name)

let input name = TaskInputId.Target name
let text name value = name, SubmissionValue.Text value

let fields values =
    values |> List.map (fun (name, value) -> text (field name) value) |> Map.ofList

let profile (value: ActorProfileInput) =
    [
        text (input InputTarget.ProfileDisplayName) value.DisplayName
        match value.Contact with
        | Some contact -> text (input InputTarget.ProfileContact) contact
        | None -> ()
    ]
    |> Map.ofList

let principal (value: PrincipalKey) =
    let kind, issuer, subject = PrincipalKey.parts value

    [
        text (input InputTarget.PrincipalKind) (Vocabulary.token PrincipalKind.vocabulary kind)
        text (input InputTarget.PrincipalIssuer) issuer
        text (input InputTarget.PrincipalSubject) subject
    ]
    |> Map.ofList

let grant (value: GrantInput) =
    [
        text (input InputTarget.Role) (Role.token value.Role)
        text
            TaskInputId.Scope
            (if value.Scope = GrantTarget.Installation then
                 "INSTALLATION"
             else
                 "CASE")
        match value.Scope with
        | GrantTarget.CaseReference reference -> text (field BusinessField.CaseReference) reference
        | GrantTarget.CaseId id -> text TaskInputId.CaseId (CaseId.format id)
        | GrantTarget.Installation -> ()
    ]
    |> Map.ofList

let command (submission: OperationSubmission) =
    match submission.Operation with
    | Ok value -> value
    | Error refusal -> failwithf "Invalid fixture: %A" refusal

let updates values =
    values
    |> List.map (fun (name, value) ->
        field name,
        value
        |> Option.map SubmissionValue.Text
        |> Option.defaultValue SubmissionValue.Clear)
    |> Map.ofList

let work (value: CaseWorkInput) =
    [
        match value.Responsible with
        | WorkValue.Keep -> ()
        | WorkValue.Clear -> yield field BusinessField.ResponsibleActor, SubmissionValue.Clear
        | WorkValue.Set actor -> yield text (field BusinessField.ResponsibleActor) actor
        match value.Summary with
        | WorkValue.Keep -> ()
        | WorkValue.Clear -> yield field BusinessField.HandlingSummary, SubmissionValue.Clear
        | WorkValue.Set summary -> yield text (field BusinessField.HandlingSummary) summary
    ]
    |> Map.ofList

let reminderPlan action due =
    [
        text (input InputTarget.ReminderAction) action
        text (input InputTarget.ReminderDueDate) due
    ]
    |> Map.ofList

let withOperationId operation (submission: OperationSubmission) =
    createRaw
        (TaskId.token submission.Task)
        operation
        submission.Envelope.Binding
        submission.Envelope.Inputs
