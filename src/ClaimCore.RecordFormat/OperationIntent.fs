namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// Business identity contains the typed public command and canonical input values only.
/// Recording time, sequence, results, representation and compartment nonces never enter it.
module OperationIntent =
    let private bytes operation command input =
        let command =
            match OperationPublicCodecs.command.Write command with
            | JsonValue.Object fields ->
                let fields =
                    match command with
                    | CommandSkeleton.Case scope ->
                        fields
                        |> List.choose (fun (key, value) ->
                            if key = "reference" && scope.Act <> CaseActKind.Open then
                                None
                            elif key = "reference" then
                                Some(
                                    key,
                                    JsonValue.String(
                                        CaseReference.canonical scope.Reference
                                        |> CaseReference.format
                                    )
                                )
                            else
                                Some(key, value))
                    | _ -> fields

                JsonValue.Object fields
            | _ -> invalidOp "An operation command is a closed object."

        JsonValue.Object
            [
                "operationId", JsonValue.String(OperationId.format operation)
                "command", command
                "input", input
            ]
        |> CanonicalWriter.bytes

    let case operation scope command =
        bytes
            operation
            (CommandSkeleton.Case scope)
            ((OperationContentCodecs.caseInput operation scope).Write command)

    let work operation parent expected input =
        bytes
            operation
            (CommandSkeleton.Work(parent, expected))
            (WorkRecordCodecs.update.Write input)

    let note operation command =
        match command with
        | NoteCommand.AddNote(parent, text) ->
            bytes
                operation
                (CommandSkeleton.Note(NoteCommandSkeleton.Add parent))
                (JsonValue.String(NoteText.format text))
        | NoteCommand.RemoveNote(parent, id) ->
            bytes
                operation
                (CommandSkeleton.Note(NoteCommandSkeleton.Remove(parent, id)))
                JsonValue.Null

    let reminder operation scope command =
        bytes
            operation
            (CommandSkeleton.Reminder scope)
            ((OperationContentCodecs.reminderInput scope).Write command)

    let authority operation scope profile =
        bytes
            operation
            (CommandSkeleton.Authority scope)
            (profile
             |> Option.map OperationContentCodecs.profile.Write
             |> Option.defaultValue JsonValue.Null)

    let accepted (value: RetainedOperation) =
        let root = value.Root

        match root.Structure.Command, value.Values with
        | CommandSkeleton.Case scope, RetainedOperationValues.Case(Some content) ->
            Some(case root.Header.OperationId scope content.Command)
        | CommandSkeleton.Work(parent, expected), RetainedOperationValues.Work(Some content) ->
            Some(work root.Header.OperationId parent expected content.Update)
        | CommandSkeleton.Note _, RetainedOperationValues.Note(Some content) ->
            Some(note root.Header.OperationId content.Command)
        | CommandSkeleton.Reminder scope, RetainedOperationValues.Reminder(Some content) ->
            Some(reminder root.Header.OperationId scope content.Command)
        | CommandSkeleton.Authority scope,
          RetainedOperationValues.Authority(RetainedProfile.Available profile) ->
            Some(authority root.Header.OperationId scope (Some profile))
        | CommandSkeleton.Authority scope,
          RetainedOperationValues.Authority RetainedProfile.NotRequired ->
            Some(authority root.Header.OperationId scope None)
        | CommandSkeleton.Case scope, RetainedOperationValues.Case None when
            (match scope.Act with
             | CaseActKind.RemovePayment
             | CaseActKind.WithdrawDecision
             | CaseActKind.Close
             | CaseActKind.Reopen
             | CaseActKind.Erase -> true
             | CaseActKind.Open
             | CaseActKind.AmendRegistration
             | CaseActKind.Correct
             | CaseActKind.Decide
             | CaseActKind.RecordPayment
             | CaseActKind.Void
             | CaseActKind.Reinstate -> false)
            ->
            Some(bytes root.Header.OperationId (CommandSkeleton.Case scope) JsonValue.Null)
        | CommandSkeleton.Reminder scope, RetainedOperationValues.Reminder None when
            scope.Act = ReminderActKind.Complete || scope.Act = ReminderActKind.Cancel
            ->
            Some(bytes root.Header.OperationId (CommandSkeleton.Reminder scope) JsonValue.Null)
        | _,
          (RetainedOperationValues.Case None | RetainedOperationValues.Work None | RetainedOperationValues.Note None | RetainedOperationValues.Reminder None | RetainedOperationValues.Authority RetainedProfile.Unavailable) ->
            None
        | _ -> invalidOp "Decoded values must belong to their operation command."
