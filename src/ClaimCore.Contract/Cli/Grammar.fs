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

    let private text =
        function
        | JsonValue.String value -> value
        | _ -> raise Usage

    let private array =
        function
        | JsonValue.Array values -> values
        | _ -> raise Usage

    let private named key value = memberValue key value |> text
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

    let private descriptor category choose descriptors =
        memberValue category descriptors
        |> array
        |> List.find (fun row -> choose (named "kind" row))

    let private fields rows options =
        rows
        |> array
        |> List.map (fun row ->
            let key = named "fieldName" row
            key, required key options |> str)
        |> obj

    let private correction inputs options =
        memberValue "groups" inputs
        |> array
        |> List.map (fun group ->
            let name = named "name" group
            let mode = required name options |> _.ToUpperInvariant()

            if
                not (memberValue "actions" group |> array |> List.map text |> List.contains mode)
            then
                raise Usage

            let members = [ "mode", str mode ]

            name,
            obj (
                if mode = "REPLACE" then
                    members @ [ "values", fields (memberValue "replaceFields" group) options ]
                else
                    members
            ))
        |> obj

    let private caseAct descriptor options =
        let kind = named "kind" descriptor
        let inputs = memberValue "inputs" descriptor

        let values =
            if named "kind" inputs = "CORRECTION_GROUPS" then
                [ "groups", correction inputs options ]
            else
                let values = fields (memberValue "fields" inputs) options

                match values with
                | JsonValue.Object [] -> []
                | JsonValue.Object [ "reason", reason ] -> [ "reason", reason ]
                | _ -> [ "values", values ]

        obj ([ "kind", str kind ] @ values)

    let private identity newId options =
        take "operationId" options |> Option.defaultWith newId |> str

    let private caseMutation newId descriptors verb operands options =
        let reference =
            match operands with
            | [ value ] -> value
            | _ -> raise Usage

        let command = descriptor "commands" (caseVerb >> (=) verb) descriptors
        let kind = named "kind" command

        let revision =
            if kind = "OPEN" then
                JsonValue.Integer 0L
            else
                required "expectedRevision" options |> number

        obj
            [
                "scope", str "CASE"
                "operationId", identity newId options
                "caseReference", str reference
                "expectedRevision", revision
                "act", caseAct command options
            ]

    let private authorityMutation newId descriptors verb operands options =
        if not (List.isEmpty operands) then
            raise Usage

        let command =
            descriptor "authorityCommands" (authorityVerbs >> List.contains verb) descriptors

        let kind = named "kind" command

        let target =
            [ "kind"; "issuer"; "subject" ]
            |> List.map (fun key -> key, required key options |> str)
            |> obj

        let values =
            match kind with
            | "SET_ENABLED" -> [ "enabled", JsonValue.Boolean(verb = "enable") ]
            | "SET_GRANT" ->
                let role = required "role" options |> str

                let scope =
                    match take "case" options with
                    | None -> obj [ "kind", str "INSTALLATION" ]
                    | Some reference -> obj [ "kind", str "CASE"; "caseReference", str reference ]

                [ "role", role; "scope", scope; "active", JsonValue.Boolean(verb = "grant") ]
            | _ -> []

        obj
            [
                "scope", str "AUTHORITY"
                "operationId", identity newId options
                "target", target
                "act", obj ([ "kind", str kind ] @ values)
            ]

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
                | "case" -> caseMutation newId descriptors verb rest options
                | "authority" -> authorityMutation newId descriptors verb rest options
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

    let private describedInputs inputs =
        if named "kind" inputs = "CORRECTION_GROUPS" then
            memberValue "groups" inputs
            |> array
            |> List.map (fun group ->
                let modes =
                    memberValue "actions" group
                    |> array
                    |> List.map (text >> _.ToLowerInvariant())
                    |> String.concat "|"

                "--" + named "name" group + " " + modes)
        else
            memberValue "fields" inputs
            |> array
            |> List.map (fun field -> "--" + (named "fieldName" field |> kebab) + " VALUE")

    let description descriptors =
        let cases =
            memberValue "commands" descriptors
            |> array
            |> List.map (fun command ->
                let kind = named "kind" command
                let revision = if kind = "OPEN" then [] else [ "--expected-revision N" ]

                String.concat
                    " "
                    ([ "case"; caseVerb kind; "REF" ]
                     @ revision
                     @ describedInputs (memberValue "inputs" command)))

        let authority =
            memberValue "authorityCommands" descriptors
            |> array
            |> List.collect (fun command ->
                let token = named "kind" command

                authorityVerbs token
                |> List.map (fun verb ->
                    "authority "
                    + verb
                    + " --kind KIND --issuer ISSUER --subject SUBJECT"
                    + (if token = "SET_GRANT" then
                           " --role ROLE [--case REF]"
                       else
                           "")))

        let reads =
            Api.all
            |> List.filter (fun endpoint -> not (Api.mutates endpoint))
            |> List.map (Api.id >> fun id -> "schema " + id + " request")

        String.concat "\n" (cases @ authority @ reads)
