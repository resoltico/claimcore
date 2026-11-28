namespace ClaimCore.RecordFormat

open System
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.Json

/// Canonical intent excludes the recording header, effects and compartment nonces.
/// Resolved aliases and permanent scalar spelling do not invent a different business intent.
module OperationIntent =
    let private target resolve =
        function
        | CaseSelector.Reference reference as selector ->
            match resolve selector with
            | Some id -> CaseSelector.Id id
            | None ->
                CaseSelector.Reference(
                    CaseReference.parse reference
                    |> Result.map CaseReference.identity
                    |> Result.defaultValue reference
                )
        | selector -> selector

    let resolveBinding resolve =
        function
        | Binding.Case(selector, revision) -> Binding.Case(target resolve selector, revision)
        | Binding.CasePayment(selector, revision, payment) ->
            Binding.CasePayment(target resolve selector, revision, payment)
        | Binding.CaseErase(selector, revision, sequence) ->
            Binding.CaseErase(target resolve selector, revision, sequence)
        | Binding.Work(selector, revision) -> Binding.Work(target resolve selector, revision)
        | Binding.NoteNew selector -> Binding.NoteNew(target resolve selector)
        | Binding.Note(selector, id) -> Binding.Note(target resolve selector, id)
        | Binding.AttachmentNew selector -> Binding.AttachmentNew(target resolve selector)
        | Binding.Attachment(selector, id) -> Binding.Attachment(target resolve selector, id)
        | Binding.PartyNew selector -> Binding.PartyNew(target resolve selector)
        | Binding.Party(selector, id, revision) ->
            Binding.Party(target resolve selector, id, revision)
        | Binding.LinkNew selector -> Binding.LinkNew(target resolve selector)
        | Binding.Link(selector, id) -> Binding.Link(target resolve selector, id)
        | Binding.ReminderNew selector -> Binding.ReminderNew(target resolve selector)
        | Binding.Reminder(selector, id, revision) ->
            Binding.Reminder(target resolve selector, id, revision)
        | value -> value

    let private uuid raw =
        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty -> value.ToString "D"
        | _ -> raw

    let private scalar input =
        function
        | InputValue.Text raw ->
            let value =
                match input with
                | TaskInputId.Target(InputTarget.Field BusinessField.CaseReference) ->
                    CaseReference.parse raw
                    |> Result.map CaseReference.identity
                    |> Result.defaultValue raw
                | TaskInputId.Target(InputTarget.Field BusinessField.ResponsibleActor)
                | TaskInputId.CaseId
                | TaskInputId.ActorId -> uuid raw
                | TaskInputId.Target(InputTarget.Field field) when
                    (BusinessField.describe field).Kind = FieldValueKind.Amount
                    ->
                    Amount.parse raw |> Result.map Amount.format |> Result.defaultValue raw
                | _ -> raw

            InputValue.Text value
        | value -> value

    let private values resolve (definitions: TaskInputDeclaration list) inputValues =
        let normalized =
            inputValues
            |> Map.toList
            |> List.choose (fun (input, value) ->
                let definition =
                    definitions |> List.tryFind (fun definition -> definition.Input = input)

                if
                    value = InputValue.Null
                    && (definition
                        |> Option.exists (fun definition ->
                            definition.Mode = TaskInputMode.Optional && definition.Clearable))
                then
                    None
                else
                    Some(input, scalar input value))
            |> Map.ofList

        match
            Map.tryFind TaskInputId.Scope normalized,
            Map.tryFind
                (TaskInputId.Target(InputTarget.Field BusinessField.CaseReference))
                normalized
        with
        | Some(InputValue.Text "CASE"), Some(InputValue.Text reference) ->
            match resolve (CaseSelector.Reference reference) with
            | Some id ->
                normalized
                |> Map.remove (TaskInputId.Target(InputTarget.Field BusinessField.CaseReference))
                |> Map.add TaskInputId.CaseId (InputValue.Text(CaseId.format id))
            | None -> normalized
        | _ -> normalized

    let reviewCommitment (token: string) =
        token
        |> System.Text.Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower

    let private intentBinding =
        function
        | Binding.CaseErase(selector, revision, review) ->
            let digest =
                match review with
                | ErasureReview.Token token -> reviewCommitment token
                | ErasureReview.Accepted(_, commitment) -> commitment

            JsonValue.Object
                [
                    "kind", JsonValue.String "CASE"
                    "target", BindingCodecs.caseSelector.Write selector
                    "expectedRevision", JsonValue.Integer(Revision.value revision)
                    "reviewCommitment", JsonValue.String digest
                ]
        | binding -> BindingCodecs.binding.Write binding

    let canonical (declaration: TaskDeclaration) supplied (inputs: InputMap) resolve =
        let scalarValues = values resolve declaration.Inputs inputs.Values

        let collections =
            inputs.Collections
            |> Map.map (fun collection rows ->
                let fields =
                    declaration.Collections
                    |> List.find (fun definition -> definition.Collection = collection)
                    |> _.Inputs

                rows |> List.map (fun row -> values resolve fields row.Values))

        let normalized = InputMap.create scalarValues collections |> Option.get

        JsonValue.Object
            [
                "task", JsonValue.String(TaskId.token declaration.Id)
                "binding", intentBinding (resolveBinding resolve supplied)
                "inputs", InputMapCodecs.stored.Write normalized
            ]
        |> CanonicalWriter.bytes

    let hash declaration binding inputs resolve =
        canonical declaration binding inputs resolve |> SHA256.HashData

    let accepted (record: RetainedOperation) resolve =
        record.Inputs
        |> Option.bind (fun inputs ->
            ClaimCore.Domain.Tasks.Registry.findDeclaration record.Root.Structure.Task
            |> Option.map (fun declaration ->
                hash declaration record.Root.Structure.Binding inputs resolve))

    let same (left: byte array) (right: byte array) =
        CryptographicOperations.FixedTimeEquals(left, right)
