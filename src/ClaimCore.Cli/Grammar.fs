namespace ClaimCore.Cli

open System
open System.Globalization
open ClaimCore.Contract
open ClaimCore.Contract.Protocol

type PreparedVerb =
    {
        Bytes: byte array
        DryRun: bool
        Text: bool
        Language: string
    }

/// Authored syntax refusals are Result values.
module Grammar =
    type private UsageResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom value = value

    let private result = UsageResultBuilder()
    let private guard condition = if condition then Ok() else Error()

    let private choose =
        function
        | Some value -> Ok value
        | None -> Error()

    let private obj = JsonValue.Object
    let private str = JsonValue.String

    let private fold action initial values =
        values
        |> List.fold
            (fun state value -> state |> Result.bind (fun current -> action current value))
            (Ok initial)

    let private flags (arguments: string list) =
        let rec collect operands options (remaining: string list) =
            match remaining with
            | [] -> Ok(List.rev operands, options)
            | flag :: tail when flag.StartsWith("--", StringComparison.Ordinal) ->
                if Map.containsKey flag options then
                    Error()
                elif List.contains flag [ "--dry-run"; "--preview"; "--full" ] then
                    collect operands (Map.add flag "true" options) tail
                else
                    match tail with
                    | value :: rest -> collect operands (Map.add flag value options) rest
                    | [] -> Error()
            | value :: tail -> collect (value :: operands) options tail

        collect [] Map.empty arguments

    let private take key (options: Map<string, string> ref) =
        let flag = "--" + CliSyntax.kebab key
        let value = Map.tryFind flag options.Value
        options.Value <- Map.remove flag options.Value
        value

    let private number (value: string) =
        match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed when parsed <= JsonValue.MaximumInteger -> Ok(JsonValue.Integer parsed)
        | _ -> Error()

    let rec private valueAt path value =
        match path with
        | [] -> Some value
        | name :: rest -> JsonValue.tryMember name value |> Option.bind (valueAt rest)

    let rec private assign path value target =
        match path with
        | [] -> value
        | name :: rest ->
            let members =
                match target with
                | JsonValue.Object members -> members
                | _ -> []

            let child =
                members
                |> List.tryFind (fst >> (=) name)
                |> Option.map snd
                |> Option.defaultValue (obj [])

            obj ((members |> List.filter (fst >> (<>) name)) @ [ name, assign rest value child ])

    let private constant =
        function
        | BindingValue.Text value -> str value
        | BindingValue.Boolean value -> JsonValue.Boolean value
        | BindingValue.Integer value -> JsonValue.Integer value

    let private assignments values target =
        values
        |> List.fold
            (fun state (value: BindingAssignment) -> assign value.Path (constant value.Value) state)
            target

    let private applicable target (input: CliInput) =
        input.Binding.Branches
        |> List.forall (fun branch -> valueAt branch.Path target = Some(str branch.Value))

    let private supplied (options: Map<string, string> ref) (input: CliInput) =
        match input.Argument with
        | Some(InvocationArgument.Flag name) -> Map.containsKey ("--" + name) options.Value
        | _ -> false

    let private operand newId operands options target (input: CliInput) =
        let value =
            match input.Argument with
            | Some(InvocationArgument.Positional index) -> List.tryItem index operands
            | Some(InvocationArgument.Flag name) -> take name options
            | None -> None

        match input.Binding.Source, value with
        | InputSource.OperationIdentity, None -> Ok(str (newId ()))
        | _, None -> valueAt input.Binding.Path target |> choose
        | _, Some value ->
            match input.Binding.Kind with
            | BindingKind.Integer -> number value
            | BindingKind.Boolean -> Error()
            | BindingKind.Group _ -> Ok(str (value.ToUpperInvariant()))
            | _ -> Ok(str value)

    let private bindInputs newId operands options target inputs =
        inputs
        |> fold
            (fun state input ->
                result {
                    let! value = operand newId operands options state input
                    return assign input.Binding.Path value state
                })
            target

    let private findInteraction (definitions: CliInteraction list) family verb =
        let scope =
            match family with
            | "case" -> Some OperationScopes.Case
            | "authority" -> Some OperationScopes.Authority
            | _ -> None

        definitions
        |> List.collect (fun interaction ->
            interaction.Invocations |> List.map (fun alias -> interaction, alias))
        |> List.tryFind (fun (interaction, alias) ->
            Some interaction.Definition.Scope = scope && alias.Verb = verb)
        |> choose

    let private positions (inputs: CliInput list) =
        let positions =
            inputs
            |> List.choose (fun input ->
                match input.Argument with
                | Some(InvocationArgument.Positional index) -> Some index
                | _ -> None)

        if positions.IsEmpty then 0 else List.max positions + 1

    let private mutation newId definitions family verb operands options =
        result {
            let! interaction, alias = findInteraction definitions family verb

            let initial =
                obj []
                |> assignments interaction.Definition.Constants
                |> assignments alias.Constants

            let selected =
                interaction.Inputs
                |> List.fold
                    (fun state input ->
                        if supplied options input then
                            assignments input.WhenPresent state
                        else
                            state)
                    initial
            // Select tagged groups before deciding which replacement fields are required.
            let selectors =
                interaction.Inputs
                |> List.filter (fun input ->
                    match input.Binding.Kind with
                    | BindingKind.Group _ -> applicable selected input
                    | _ -> false)

            let! selected = bindInputs newId operands options selected selectors
            let inputs = interaction.Inputs |> List.filter (applicable selected)
            do! guard (List.length operands = positions inputs)
            return! bindInputs newId operands options selected inputs
        }

    let private page (metadata: PresentationDescriptors) options =
        result {
            let cursor =
                take "cursor" options
                |> Option.map (fun value -> "cursor", str value)
                |> Option.toList

            let! limit =
                match take "limit" options with
                | Some value -> number value
                | None -> Ok(JsonValue.Integer(int64 metadata.Limits.DefaultPageSize))

            return cursor @ [ "limit", limit ]
        }

    let private read metadata command operands options =
        let one () =
            match operands with
            | [ value ] -> Ok(str value)
            | _ -> Error()

        let reference = FieldTable.key BusinessField.CaseReference

        result {
            let! members =
                match command with
                | "case.get" -> one () |> Result.map (fun value -> [ reference, value ])
                | "operation.resolve"
                | "operation.observe" ->
                    one () |> Result.map (fun value -> [ "operationId", value ])
                | "authority.grants" ->
                    result {
                        let! actor = one ()
                        let! pagination = page metadata options
                        return ("actorId", actor) :: pagination
                    }
                | "case.history" ->
                    result {
                        let! target = one ()

                        let detail =
                            if take "full" options = Some "true" then
                                HistoryDetail.Full
                            else
                                HistoryDetail.Summary

                        let! pagination = page metadata options

                        return
                            [
                                reference, target
                                "detail", str (Vocabulary.token HistoryDetails.vocabulary detail)
                            ]
                            @ pagination
                    }
                | "case.list"
                | "authority.list" when List.isEmpty operands -> page metadata options
                | "context" when List.isEmpty operands -> Ok []
                | _ -> Error()

            return command, obj members
        }

    let private selection newId metadata definitions operands options =
        match operands with
        | "context" :: rest -> read metadata "context" rest options
        | family :: verb :: rest ->
            let command = family + "." + verb

            match Api.tryFind command with
            | Some _ -> read metadata command rest options
            | None ->
                mutation newId definitions family verb rest options
                |> Result.map (fun input ->
                    (if take "preview" options = Some "true" then
                         "operation.preview"
                     else
                         "operation.execute"),
                    input)
        | _ -> Error()

    let internal prepareWith newId metadata definitions arguments =
        result {
            let! operands, values = flags arguments
            let options = ref values
            let dryRun = take "dryRun" options = Some "true"
            let format = take "format" options |> Option.defaultValue "text"
            let language = take "language" options |> Option.defaultValue "en"

            do!
                guard (
                    List.contains format [ "text"; "json" ]
                    && List.contains language LocalizationPolicy.languages
                )

            let! endpoint, input = selection newId metadata definitions operands options
            do! guard options.Value.IsEmpty
            let bytes = obj [ "endpoint", str endpoint; "input", input ] |> WireWriter.bytes
            let! _ = Frames.admit bytes |> Result.mapError (fun _ -> ())

            return
                {
                    Bytes = bytes
                    DryRun = dryRun
                    Text = format = "text"
                    Language = language
                }
        }

    let prepare newId descriptors arguments =
        result {
            let! metadata =
                DescriptorCodecs.codec.Read "$" descriptors |> Result.mapError (fun _ -> ())

            let! definitions = CliSyntax.compile metadata
            return! prepareWith newId metadata definitions arguments
        }

    let private argumentDescription (input: CliInput) =
        match input.Argument with
        | None -> None
        | Some(InvocationArgument.Positional _) -> Some "REF"
        | Some(InvocationArgument.Flag name) ->
            let value =
                match input.Binding.Kind with
                | BindingKind.Group options ->
                    options |> List.map _.ToLowerInvariant() |> String.concat "|"
                | BindingKind.Integer -> "N"
                | _ -> "VALUE"

            let text = "--" + name + " " + value

            let optional =
                input.Binding.Source = InputSource.OperationIdentity
                || not input.WhenPresent.IsEmpty
                || not input.Binding.Branches.IsEmpty

            Some(if optional then "[" + text + "]" else text)

    let description value =
        result {
            let! metadata =
                DescriptorCodecs.codec.Read "$" value |> Result.mapError (fun _ -> ())

            let! definitions = CliSyntax.compile metadata

            let mutations =
                definitions
                |> List.collect (fun interaction ->
                    interaction.Invocations
                    |> List.map (fun alias ->
                        let family =
                            if interaction.Definition.Scope = OperationScopes.Case then
                                "case"
                            else
                                "authority"

                        String.concat
                            " "
                            ([ family; alias.Verb ]
                             @ List.choose argumentDescription interaction.Inputs)))

            let reads =
                Api.all
                |> List.filter (fun endpoint ->
                    Api.effect endpoint <> EndpointEffect.LedgerExecution)
                |> List.map (Api.id >> fun id -> "schema " + id + " request")

            return String.concat "\n" (mutations @ reads @ [ "operation resolve OPERATION_ID" ])
        }
