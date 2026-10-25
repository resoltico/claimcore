namespace ClaimCore.Cli

open System.Text.RegularExpressions
open ClaimCore.Contract

[<RequireQualifiedAccess>]
type internal InvocationArgument =
    | Flag of string
    | Positional of int

type internal CliVerb =
    {
        Verb: string
        Constants: BindingAssignment list
    }

type internal CliInput =
    {
        Binding: CatalogInput
        Argument: InvocationArgument option
        WhenPresent: BindingAssignment list
    }

type internal CliOperation =
    {
        Definition: CatalogEntry
        Inputs: CliInput list
        Invocations: CliVerb list
    }

/// Command-line spelling and defaults belong to this client, alongside its parser.
module internal CliSyntax =
    let kebab (value: string) =
        Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").Replace('_', '-').ToLowerInvariant()

    let caseVerb token = kebab token

    let private reference = FieldTable.key BusinessField.CaseReference
    let private caseScope = [ "act"; "scope"; reference ]

    let private text path value =
        {
            Path = path
            Value = BindingValue.Text value
        }

    let private argument scope (input: CatalogInput) =
        match input.Path with
        | path when scope = OperationScopes.Case && path = [ reference ] ->
            Some(InvocationArgument.Positional 0)
        | path when path = caseScope -> Some(InvocationArgument.Flag "case")
        | [ "act"; "scope"; "kind" ] -> None
        | [ "act"; "groups"; group; "mode" ] -> Some(InvocationArgument.Flag group)
        | _ when input.Kind = BindingKind.Boolean -> None
        | path -> List.tryLast path |> Option.map (kebab >> InvocationArgument.Flag)

    let private bind scope input =
        {
            Binding = input
            Argument = argument scope input
            WhenPresent =
                if input.Path = caseScope then
                    [ text [ "act"; "scope"; "kind" ] "CASE" ]
                else
                    []
        }

    let private authority kind =
        match AuthorityKinds.parse kind with
        | Some AuthorityKind.ProvisionOwner
        | None -> None
        | Some value ->
            Some
                [
                    {
                        Verb = kebab kind
                        Constants =
                            match value with
                            | AuthorityKind.Grant
                            | AuthorityKind.Revoke ->
                                [ text [ "act"; "scope"; "kind" ] "INSTALLATION" ]
                            | _ -> []
                    }
                ]

    let private interaction (definition: CatalogEntry) =
        let invocations =
            if
                definition.Scope = OperationScopes.Case
                && (CaseActKinds.parse definition.Kind |> Option.isSome)
            then
                let name = caseVerb definition.Kind

                Some [ { Verb = name; Constants = [] } ]
            elif definition.Scope = OperationScopes.Authority then
                authority definition.Kind
            else
                None

        match invocations with
        | None -> Error()
        | Some aliases ->
            Ok
                {
                    Definition = definition
                    Inputs = List.map (bind definition.Scope) definition.Inputs
                    Invocations = aliases
                }

    let private validate (definitions: CliOperation list) =
        let aliases =
            definitions
            |> List.collect (fun row ->
                row.Invocations |> List.map (fun alias -> row.Definition.Scope, alias.Verb))

        let uniqueArguments row =
            let arguments = row.Inputs |> List.choose _.Argument
            List.distinct arguments = arguments

        if
            List.distinct aliases <> aliases
            || not (List.forall uniqueArguments definitions)
        then
            Error()
        else
            Ok definitions

    let compile (metadata: CatalogDocument) =
        metadata.Entries
        |> List.filter (fun entry -> entry.Endpoint = Api.operationExecute.Id)
        |> List.fold
            (fun state item ->
                state
                |> Result.bind (fun rows ->
                    interaction item |> Result.map (fun row -> row :: rows)))
            (Ok [])
        |> Result.map List.rev
        |> Result.bind validate
