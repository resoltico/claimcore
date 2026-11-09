namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// Cross references and disclosure constrain interaction data independently of business admission.
module internal HumanDocumentStructure =
    let private array name value =
        match JsonValue.tryMember name value with
        | Some(JsonValue.Array values) -> values
        | _ -> []

    let private memberValue name value =
        JsonValue.tryMember name value |> Option.defaultValue JsonValue.Null

    let private unique values =
        (List.distinct values).Length = values.Length

    let private inputs group =
        BusinessField.ofGroup group
        |> List.map (
            InputTarget.Field >> TaskInputId.Target >> TaskInputId.token >> JsonValue.String
        )

    let validate path value =
        let context = memberValue "context" value
        let tasks = array "tasks" context
        let editable = array "editableInputs" context

        let declared =
            tasks
            |> List.collect (function
                | JsonValue.String key ->
                    TaskId.all
                    |> List.tryFind (fun task -> TaskId.key task = key)
                    |> Option.map (
                        TaskDeclarations.describe
                        >> _.Inputs
                        >> List.map (fun input -> JsonValue.String(TaskInputId.token input.Input))
                    )
                    |> Option.defaultValue []
                | _ -> [])
            |> Set.ofList

        let editableBindings =
            editable |> List.forall (fun input -> Set.contains input declared)

        let seeds = memberValue "seeds" context

        let supplied field =
            memberValue field seeds <> JsonValue.Null

        let active = memberValue "disclosure" value = JsonValue.String "CURRENT"

        let inputPresent field =
            List.contains
                (JsonValue.String(TaskInputId.token (TaskInputId.Target(InputTarget.Field field))))
                editable

        let protectedSeeds =
            not (supplied "registration" || supplied "decision" || supplied "work")
            || active

        let seedBindings =
            (not (supplied "registration")
             || inputs RecordGroup.Registration
                |> List.forall (fun input -> List.contains input editable))
            && (not (supplied "decision")
                || inputs RecordGroup.Decision
                   |> List.forall (fun input -> List.contains input editable))
            && (not (supplied "work")
                || let work = memberValue "work" seeds in

                   (memberValue "summary" work = JsonValue.Null
                    || inputPresent BusinessField.HandlingSummary)
                   && (memberValue "responsible" work = JsonValue.Null
                       || inputPresent BusinessField.ResponsibleActor))

        let reminderBindings =
            if memberValue "kind" context <> JsonValue.String "REMINDER" then
                true
            else
                let action = memberValue "actionSeed" context <> JsonValue.Null
                let due = memberValue "dueDateSeed" context <> JsonValue.Null

                let declaredInput target =
                    Set.contains
                        (JsonValue.String(TaskInputId.token (TaskInputId.Target target)))
                        declared

                (not (action || due) || active)
                && (not action || declaredInput InputTarget.ReminderAction)
                && (not due || declaredInput InputTarget.ReminderDueDate)

        let correct =
            JsonValue.String(TaskId.key (TaskId.Operation(ActKind.Case CaseActKind.Correct)))

        let correctionBinding =
            memberValue "kind" context <> JsonValue.String "CASE"
            || List.contains correct tasks = (memberValue "correction" context <> JsonValue.Null)

        let actions =
            memberValue "screen" value
            |> array "regions"
            |> List.collect (fun region ->
                let content = memberValue "content" region

                if memberValue "kind" content <> JsonValue.String "ACTIONS" then
                    []
                else
                    (match memberValue "recommended" content with
                     | JsonValue.Null -> []
                     | item -> [ item ])
                    @ (array "groups" content |> List.collect (array "acts")))

        let knownActions =
            actions
            |> List.forall (function
                | JsonValue.String token ->
                    ActKind.parse token
                    |> Option.exists (fun act ->
                        List.contains (JsonValue.String(TaskId.key (TaskId.Operation act))) tasks)
                | _ -> false)

        if
            unique tasks
            && unique editable
            && unique actions
            && editableBindings
            && reminderBindings
            && protectedSeeds
            && seedBindings
            && correctionBinding
            && knownActions
        then
            Ok()
        else
            Error
                {
                    Path = path
                    Reason =
                        "Human interaction references and seeds must match the offered tasks and disclosure."
                }
