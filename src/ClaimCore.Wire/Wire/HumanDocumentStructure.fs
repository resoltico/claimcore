namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

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

    let private reviewBinding context =
        let task =
            TaskId.all
            |> List.tryFind (fun task ->
                memberValue "task" context = JsonValue.String(TaskId.key task))

        match task with
        | Some(TaskId.Operation act as task) ->
            let declaration = TaskDeclarations.describe task

            let expectedInputs =
                (declaration.Inputs @ (declaration.Collections |> List.collect _.Inputs))
                |> List.map (fun input -> JsonValue.String(TaskInputId.token input.Input))
                |> Set.ofList

            let supplied = array "inputs" context
            let expectedTarget = ActSubject.token (ActKind.subject act)

            let confirmationKind =
                if ActKind.subject act = ActSubject.Authority then
                    "OPERATION"
                else
                    "CASE_REFERENCE"

            unique supplied
            && Set.ofList supplied = expectedInputs
            && memberValue "kind" (memberValue "target" context) = JsonValue.String expectedTarget
            && memberValue "kind" (memberValue "confirmation" context) =
                JsonValue.String confirmationKind
            && memberValue "consequence" context =
                JsonValue.String(Vocabulary.token Consequence.vocabulary (Consequence.ofAct act))
        | _ -> false

    let private operationBinding value context =
        match memberValue "kind" context with
        | JsonValue.String "REVIEW" ->
            memberValue "disclosure" value = JsonValue.String "CURRENT"
            && memberValue "kind" (memberValue "basis" value) = JsonValue.String "SNAPSHOT"
            && reviewBinding context
        | JsonValue.String "RECEIPT" ->
            let target = memberValue "target" context
            let versions = array "versions" target
            let ids = versions |> List.map (memberValue "reminderId")

            let refresh = memberValue "refreshCase" context

            unique ids
            && (refresh = JsonValue.Null || refresh = memberValue "caseId" target)
            && memberValue "kind" (memberValue "basis" value) = JsonValue.String "SNAPSHOT"
        | _ -> true

    let private paymentBindings value context =
        if memberValue "kind" context <> JsonValue.String "PAYMENT_PAGE" then
            true
        else
            let handles = array "payments" context
            let ids = handles |> List.map (memberValue "paymentId")

            let keys =
                memberValue "screen" value
                |> array "regions"
                |> List.filter (fun region ->
                    memberValue "role" region = JsonValue.String "PAYMENTS")
                |> List.collect (fun region ->
                    memberValue "content" region |> array "rows" |> List.map (memberValue "key"))

            unique ids
            && Set.ofList ids = Set.ofList keys
            && (memberValue "disclosure" value = JsonValue.String "CURRENT" || handles.IsEmpty)

    let private assigneeBindings value context =
        if memberValue "kind" context <> JsonValue.String "ASSIGNEES" then
            true
        else
            let ids = array "actorIds" context

            let keys =
                memberValue "screen" value
                |> array "regions"
                |> List.filter (fun region ->
                    memberValue "role" region = JsonValue.String "RESULTS")
                |> List.collect (fun region ->
                    memberValue "content" region |> array "rows" |> List.map (memberValue "key"))

            unique ids
            && ids = keys
            && (memberValue "disclosure" value = JsonValue.String "CURRENT" || ids.IsEmpty)

    let private navigationBindings value context =
        let ids =
            match memberValue "kind" context with
            | JsonValue.String "CASES" -> Some(array "caseIds" context)
            | JsonValue.String "REMINDERS" ->
                Some(array "reminders" context |> List.map (memberValue "reminderId"))
            | JsonValue.String "PEOPLE" -> Some(array "actorIds" context)
            | JsonValue.String "ACCESS_REQUESTS" ->
                Some(array "requests" context |> List.map (memberValue "key"))
            | _ -> None

        match ids with
        | None -> true
        | Some ids ->
            let keys =
                memberValue "screen" value
                |> array "regions"
                |> List.filter (fun region ->
                    memberValue "role" region = JsonValue.String "RESULTS")
                |> List.collect (fun region ->
                    memberValue "content" region |> array "rows" |> List.map (memberValue "key"))

            unique ids
            && ids = keys
            && (memberValue "disclosure" value = JsonValue.String "CURRENT" || ids.IsEmpty)

    let private noteBindings value context =
        match memberValue "kind" context with
        | JsonValue.String "CASE"
        | JsonValue.String "NOTE_PAGE" ->
            let handles = array "notes" context
            let ids = handles |> List.map (memberValue "noteId")

            let keys =
                memberValue "screen" value
                |> array "regions"
                |> List.filter (fun region -> memberValue "role" region = JsonValue.String "NOTES")
                |> List.collect (fun region ->
                    memberValue "content" region |> array "rows" |> List.map (memberValue "key"))

            let tasks = array "tasks" context

            let noteTask kind =
                JsonValue.String(TaskId.key (TaskId.Operation(ActKind.Note kind)))

            let removes =
                handles
                |> List.exists (fun item -> memberValue "canRemove" item = JsonValue.Boolean true)

            unique ids
            && ids = keys
            && (memberValue "disclosure" value = JsonValue.String "CURRENT" || handles.IsEmpty)
            && (List.contains (noteTask NoteActKind.RemoveNote) tasks = removes)
            && (memberValue "kind" context <> JsonValue.String "NOTE_PAGE"
                || tasks
                   |> List.forall (fun task ->
                       List.contains
                           task
                           [ noteTask NoteActKind.AddNote; noteTask NoteActKind.RemoveNote ]))
        | _ -> true

    let private personBindings value context =
        if memberValue "kind" context <> JsonValue.String "PERSON" then
            true
        else
            let tasks = array "tasks" context

            let allowed =
                AuthorityKind.all
                |> List.filter AuthorityKind.targetsExistingActor
                |> List.map (
                    ActKind.Authority >> TaskId.Operation >> TaskId.key >> JsonValue.String
                )

            let profileTask =
                JsonValue.String(
                    TaskId.key (TaskId.Operation(ActKind.Authority AuthorityKind.SetProfile))
                )

            memberValue "disclosure" value = JsonValue.String "CURRENT"
            && (tasks |> List.forall (fun task -> List.contains task allowed))
            && (memberValue "profileSeed" context = JsonValue.Null
                || List.contains profileTask tasks)

    let private currentGroupBindings value context =
        let groups = memberValue "screen" value |> array "currentGroups"

        groups.IsEmpty
        || (memberValue "kind" context = JsonValue.String "CASE"
            && memberValue "disclosure" value = JsonValue.String "CURRENT"
            && groups
               |> List.forall (fun item ->
                   let field =
                       match memberValue "group" item with
                       | JsonValue.String "registration" -> "registration"
                       | JsonValue.String "decision" -> "decision"
                       | _ -> ""

                   memberValue "correction" context
                   |> array field
                   |> List.contains (JsonValue.String "UPDATE")))

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
            && operationBinding value context
            && paymentBindings value context
            && assigneeBindings value context
            && currentGroupBindings value context
            && navigationBindings value context
            && personBindings value context
            && noteBindings value context
        then
            Ok()
        else
            Error
                {
                    Path = path
                    Reason =
                        "Human interaction references and seeds must match the offered tasks and disclosure."
                }
