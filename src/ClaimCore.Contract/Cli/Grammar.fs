namespace ClaimCore.Contract

open System
open System.Globalization
open System.Text.RegularExpressions

type PreparedVerb =
    {
        Bytes: byte array
        DryRun: bool
        Text: bool
        Language: string
    }

/// CLI aliases select core tokens; input requirements come from the published descriptors.
module Grammar =
    exception private Usage

    let kebab (value: string) =
        Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").Replace('_', '-').ToLowerInvariant()

    let caseVerb token =
        if token = "CORRECT_CASE" then "correct" else kebab token

    let authorityVerbs =
        function
        | "REGISTER_ACTOR" -> [ "register" ]
        | "SET_ENABLED" -> [ "enable"; "disable" ]
        | "SET_GRANT" -> [ "grant"; "revoke" ]
        | _ -> raise Usage

    let private memberValue key value =
        JsonValue.tryMember key value |> Option.defaultWith (fun () -> raise Usage)

    let private obj = JsonValue.Object
    let private str = JsonValue.String

    let private flags (arguments: string list) =
        let rec collect operands options (remaining: string list) =
            match remaining with
            | [] -> List.rev operands, options
            | flag :: tail when flag.StartsWith("--", StringComparison.Ordinal) ->
                if Map.containsKey flag options then
                    raise Usage

                if List.contains flag [ "--dry-run"; "--preview"; "--full" ] then
                    collect operands (Map.add flag "true" options) tail
                else
                    match tail with
                    | value :: rest -> collect operands (Map.add flag value options) rest
                    | [] -> raise Usage
            | value :: tail -> collect (value :: operands) options tail

        collect [] Map.empty arguments

    let private take key (options: Map<string, string> ref) =
        let flag = "--" + kebab key
        let value = Map.tryFind flag options.Value
        options.Value <- Map.remove flag options.Value
        value

    let private required key options =
        take key options |> Option.defaultWith (fun () -> raise Usage)

    let private number (value: string) =
        match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed when parsed <= JsonValue.MaximumInteger -> JsonValue.Integer parsed
        | _ -> raise Usage

    let private descriptors value =
        DescriptorCodecs.codec.Read "$" value
        |> Result.defaultWith (fun _ -> raise Usage)

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

    let private applicable target (input: InteractionInput) =
        input.Branches
        |> List.forall (fun branch -> valueAt branch.Path target = Some(str branch.Value))

    let private supplied (options: Map<string, string> ref) (input: InteractionInput) =
        match input.Argument with
        | Some(InvocationArgument.Flag name) -> Map.containsKey ("--" + name) options.Value
        | _ -> false

    let private operand newId operands options target (input: InteractionInput) =
        let value =
            match input.Argument with
            | Some(InvocationArgument.Positional index) -> List.tryItem index operands
            | Some(InvocationArgument.Flag name) -> take name options
            | None -> None

        match input.Source, value with
        | InputSource.OperationIdentity, None -> str (newId ())
        | _, None -> valueAt input.Path target |> Option.defaultWith (fun () -> raise Usage)
        | _, Some value ->
            match input.Kind with
            | BindingKind.Integer -> number value
            | BindingKind.Boolean -> raise Usage
            | BindingKind.Group _ -> str (value.ToUpperInvariant())
            | _ -> str value

    let private mutation newId definitions family verb operands options =
        let scope =
            if family = "case" then OperationScopes.Case
            elif family = "authority" then OperationScopes.Authority
            else raise Usage

        let interaction, alias =
            (descriptors definitions).Interactions
            |> List.collect (fun interaction ->
                interaction.Invocations |> List.map (fun alias -> interaction, alias))
            |> List.tryFind (fun (interaction, alias) ->
                interaction.Scope = scope && alias.Verb = verb)
            |> Option.defaultWith (fun () -> raise Usage)

        let initial =
            obj [] |> assignments interaction.Constants |> assignments alias.Constants

        let selected =
            interaction.Inputs
            |> List.fold
                (fun state input ->
                    if supplied options input then
                        assignments input.WhenPresent state
                    else
                        state)
                initial

        let inputs = interaction.Inputs |> List.filter (applicable selected)

        let positions =
            inputs
            |> List.choose (fun input ->
                match input.Argument with
                | Some(InvocationArgument.Positional index) -> Some index
                | _ -> None)

        let count = if positions.IsEmpty then 0 else List.max positions + 1

        if List.length operands <> count then
            raise Usage

        inputs
        |> List.fold
            (fun state input ->
                assign input.Path (operand newId operands options state input) state)
            selected

    let private page descriptors options =
        let cursor =
            take "cursor" options
            |> Option.map (fun value -> "cursor", str value)
            |> Option.toList

        cursor
        @ [
            "limit",
            take "limit" options
            |> Option.map number
            |> Option.defaultValue (
                memberValue "limits" descriptors |> memberValue "defaultPageSize"
            )
        ]

    let private read descriptors command operands options =
        let one () =
            match operands with
            | [ value ] -> str value
            | _ -> raise Usage

        let members =
            match command with
            | "case.get" -> [ "caseReference", one () ]
            | "operation.resolve"
            | "operation.observe" -> [ "operationId", one () ]
            | "authority.grants" -> [ "actorId", one () ] @ page descriptors options
            | "case.history" ->
                let detail =
                    if take "full" options = Some "true" then
                        "FULL"
                    else
                        "SUMMARY"

                [ "caseReference", one (); "detail", str detail ] @ page descriptors options
            | "case.list"
            | "authority.list" when List.isEmpty operands -> page descriptors options
            | "context" when List.isEmpty operands -> []
            | _ -> raise Usage

        command, obj members

    let private selection newId descriptors operands options =
        let command, rest =
            match operands with
            | "context" :: rest -> "context", rest
            | family :: verb :: rest -> family + "." + verb, rest
            | _ -> raise Usage

        match Api.tryFind command with
        | Some _ -> read descriptors command rest options
        | None ->
            let family, verb = command.Split('.') |> fun parts -> parts[0], parts[1]

            let input =
                match family with
                | "case"
                | "authority" -> mutation newId descriptors family verb rest options
                | _ -> raise Usage

            let endpoint =
                if take "preview" options = Some "true" then
                    "operation.preview"
                else
                    "operation.execute"

            endpoint, input

    let prepare newId descriptors arguments =
        try
            let operands, values = flags arguments
            let options = ref values
            let dryRun = take "dryRun" options = Some "true"
            let format = take "format" options |> Option.defaultValue "text"
            let language = take "language" options |> Option.defaultValue "en"

            if
                not (List.contains format [ "text"; "json" ])
                || not (List.contains language LocalizationPolicy.languages)
            then
                raise Usage

            let endpoint, input = selection newId descriptors operands options

            if not options.Value.IsEmpty then
                raise Usage

            let bytes = obj [ "endpoint", str endpoint; "input", input ] |> WireWriter.bytes

            Frames.admit bytes
            |> Result.map (fun _ ->
                {
                    Bytes = bytes
                    DryRun = dryRun
                    Text = format = "text"
                    Language = language
                })
            |> Result.mapError (fun _ -> ())
        with
        | Usage
        | :? Collections.Generic.KeyNotFoundException -> Error()

    let private argumentDescription (input: InteractionInput) =
        match input.Argument with
        | None -> None
        | Some(InvocationArgument.Positional _) -> Some "REF"
        | Some(InvocationArgument.Flag name) ->
            let value =
                match input.Kind with
                | BindingKind.Group options ->
                    options |> List.map _.ToLowerInvariant() |> String.concat "|"
                | BindingKind.Integer -> "N"
                | _ -> "VALUE"

            let text = "--" + name + " " + value

            Some(
                if
                    input.Source = InputSource.OperationIdentity || not input.WhenPresent.IsEmpty
                then
                    "[" + text + "]"
                else
                    text
            )

    let description value =
        let definitions = descriptors value

        let mutations =
            definitions.Interactions
            |> List.collect (fun interaction ->
                interaction.Invocations
                |> List.map (fun alias ->
                    let family =
                        if interaction.Scope = OperationScopes.Case then
                            "case"
                        else
                            "authority"

                    String.concat
                        " "
                        ([ family; alias.Verb ]
                         @ List.choose argumentDescription interaction.Inputs)))

        let reads =
            Api.all
            |> List.filter (fun endpoint -> Api.effect endpoint <> EndpointEffect.LedgerExecution)
            |> List.map (Api.id >> fun id -> "schema " + id + " request")

        String.concat "\n" (mutations @ reads @ [ "operation resolve OPERATION_ID" ])
