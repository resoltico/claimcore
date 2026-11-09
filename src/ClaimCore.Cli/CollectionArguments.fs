namespace ClaimCore.Cli

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

/// Compound argv syntax adapts the declared collection paths, row choices and constants.
module internal CollectionArguments =
    open VerbOptions

    let private grantRow
        (collection: CatalogCollection)
        flag
        (raw: string)
        (problems: ResizeArray<UsageError>)
        =
        let role =
            collection.RowInputs
            |> List.find (fun input -> input.Field = Some InputTarget.Role)

        let scope =
            collection.RowInputs
            |> List.find (fun input ->
                match input.Kind with
                | BindingKind.Group _ -> true
                | _ -> false)

        let reference =
            collection.RowInputs
            |> List.find (fun input ->
                input.Field = Some(InputTarget.Field BusinessField.CaseReference))

        let choices =
            match role.Kind with
            | BindingKind.Choice(_, values) -> values
            | _ -> invalidOp "Grant role choices must be finite."

        let separator = raw.IndexOf('@')

        let roleText, caseReference =
            if separator < 0 then
                raw, None
            else
                raw.Substring(0, separator), Some(raw.Substring(separator + 1))

        let scopeValue =
            match caseReference with
            | Some _ ->
                reference.Branches
                |> List.find (fun branch -> branch.Path = scope.Path)
                |> _.Value
                |> JsonValue.String
            | None ->
                scope.Default
                |> Option.map bindingValue
                |> Option.defaultWith (fun () ->
                    invalidOp "A grant collection must declare its default scope.")

        let roleProblems = ResizeArray<UsageError>()
        let roleValue = atom flag (CatalogAtom.Choice choices) roleText roleProblems

        for issue in roleProblems do
            problems.Add(
                match issue with
                | UsageError.InvalidChoice(name, _, allowed) ->
                    UsageError.InvalidChoice(name, raw, allowed)
                | issue -> issue
            )

        let row =
            JsonValue.Object []
            |> insert role.Path roleValue
            |> insert scope.Path scopeValue

        let row =
            caseReference
            |> Option.map (fun value -> insert reference.Path (JsonValue.String value) row)
            |> Option.defaultValue row

        assignConstants collection.RowConstants row

    let build
        (definition: OperationCollectionDefinition)
        values
        (problems: ResizeArray<UsageError>)
        =
        let flag = CommandGrammar.collectionFlag definition
        let collection = definition.Binding

        if List.length values < collection.MinimumItems then
            problems.Add(UsageError.MissingOption flag)
            JsonValue.Null
        elif List.length values > collection.MaximumItems then
            problems.Add(
                UsageError.InvalidChoice(flag, List.item collection.MaximumItems values, [])
            )

            JsonValue.Null
        else
            match definition.Name with
            | TaskInputId.Target InputTarget.Grants ->
                values
                |> List.map (fun value -> grantRow collection flag value problems)
                |> JsonValue.Array
            | _ -> invalidOp "A declared collection requires an explicit CLI argument syntax."
