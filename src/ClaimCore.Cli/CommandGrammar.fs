namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol
open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type CatalogArgument =
    | Named
    | Operand
    | Implicit

[<RequireQualifiedAccess>]
type CatalogAtom =
    | Text
    | Integer
    | Boolean
    | Choice of string list
    | Switch of off: string * on: string

type CatalogDerivedInput =
    {
        Name: InteractionInput
        Atom: CatalogAtom
        Argument: CatalogArgument
        Binding: CatalogInput
    }

module CommandGrammar =
    let kebab (value: string) =
        Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").Replace('_', '-').ToLowerInvariant()

    let inputName =
        function
        | InteractionInput.Target InputTarget.PrincipalKind -> "principalKind"
        | InteractionInput.Target InputTarget.PrincipalIssuer -> "issuer"
        | InteractionInput.Target InputTarget.PrincipalSubject -> "subject"
        | InteractionInput.Detail -> "full"
        | input -> InteractionInput.key input

    let words (entry: CatalogOperation) =
        match entry.SelectedAct with
        | Some(ActKind.Case kind) -> [ "case"; kebab (CaseActKind.token kind) ]
        | Some(ActKind.Authority kind) -> [ "authority"; kebab (AuthorityKind.token kind) ]
        | None -> (ActorEndpoint.token entry.Endpoint).Split('.') |> List.ofArray

    let derive (operation: CatalogOperation) =
        let inputs, constants = OperationCatalog.derive operation

        let grammar (input: OperationInputDefinition) =
            let argument =
                match input.Name with
                | InteractionInput.Scope -> CatalogArgument.Implicit
                | InteractionInput.Target(InputTarget.Field BusinessField.CaseReference) when
                    input.Binding.Path = [ "caseReference" ]
                    ->
                    CatalogArgument.Operand
                | (InteractionInput.Target InputTarget.OperationId) when
                    input.Binding.Source <> InputSource.OperationIdentity
                    ->
                    CatalogArgument.Operand
                | InteractionInput.ActorId -> CatalogArgument.Operand
                | _ -> CatalogArgument.Named

            let atom =
                match input.Name, input.Binding.Kind with
                | InteractionInput.Detail, _ ->
                    CatalogAtom.Switch(
                        Vocabulary.token HistoryDetail.vocabulary HistoryDetail.Summary,
                        Vocabulary.token HistoryDetail.vocabulary HistoryDetail.Full
                    )
                | _, BindingKind.Text -> CatalogAtom.Text
                | _, BindingKind.Integer -> CatalogAtom.Integer
                | _, BindingKind.Boolean -> CatalogAtom.Boolean
                | _, BindingKind.Choice choices
                | _, BindingKind.Group choices -> CatalogAtom.Choice choices

            {
                Name = input.Name
                Atom = atom
                Argument = argument
                Binding = input.Binding
            }

        inputs |> List.map grammar, constants

    let synopsis (operation: CatalogOperation) =
        let placeholder =
            function
            | CatalogAtom.Text -> "VALUE"
            | CatalogAtom.Integer -> "N"
            | CatalogAtom.Boolean -> "true|false"
            | CatalogAtom.Choice choices -> choices |> List.map kebab |> String.concat "|"
            | CatalogAtom.Switch _ -> ""

        let inputs, _ = derive operation

        let arguments =
            inputs
            |> List.choose (fun input ->
                match input.Argument with
                | CatalogArgument.Implicit -> None
                | CatalogArgument.Operand ->
                    Some(kebab (inputName input.Name) |> _.ToUpperInvariant())
                | CatalogArgument.Named ->
                    let suffix = placeholder input.Atom

                    let text =
                        "--"
                        + kebab (inputName input.Name)
                        + (if suffix = "" then "" else " " + suffix)

                    Some(
                        if input.Binding.Required && input.Binding.Branches.IsEmpty then
                            text
                        else
                            "[" + text + "]"
                    ))

        String.concat " " (words operation @ arguments)
