namespace ClaimCore.Cli

open System
open System.IO
open System.Text
open ClaimCore.Contract

/// Human output is a rendering of admitted replies, never an input or policy mechanism.
module internal TextReply =
    let private memberValue name value =
        JsonValue.tryMember name value |> Option.defaultValue JsonValue.Null

    let private scalar =
        function
        | JsonValue.String value -> value
        | JsonValue.Integer value -> string value
        | JsonValue.Boolean value -> if value then "true" else "false"
        | JsonValue.Null -> "—"
        | value -> WireWriter.bytes value |> Encoding.UTF8.GetString

    let private get name value = memberValue name value |> scalar

    let private displayed language value =
        let text = scalar value

        match value with
        | JsonValue.String _ ->
            let warning = TextDisplay.warning text

            if warning.CodePoints.IsEmpty then
                text
            else
                let points =
                    warning.CodePoints @ (if warning.Truncated then [ "…" ] else [])
                    |> String.concat ", "

                let annotation =
                    MessageCatalogs.render
                        language
                        "ui.characterWarning"
                        (Map.ofList [ "characters", points ])

                text + " [" + annotation + "]"
        | _ -> text

    let private items =
        function
        | JsonValue.Array values -> values
        | _ -> []

    let private label language key =
        MessageCatalogs.render language key Map.empty

    let private line language key value = label language key + ": " + value

    let private fieldLines language value =
        FieldTable.rows
        |> List.map (fun row ->
            line
                language
                ("field." + row.Key + ".label")
                (memberValue row.Key value |> displayed language))

    let private stateFields state = memberValue "fields" state

    let private caseLines language value =
        let state = memberValue "state" value

        let status =
            if get "tag" state = "LIVE" then
                if get "disposition" state = "VOIDED" then
                    "VOIDED"
                else
                    get "status" (stateFields state)
            else
                get "tag" state

        let fields =
            if get "tag" state = "LIVE" then
                fieldLines language (stateFields state)
            else
                []

        let advice = memberValue "advice" value |> memberValue "acts" |> items

        [
            line language "field.caseReference.label" (get "caseReference" value)
            line language "ui.revisionLabel" (get "revision" value)
            line language "cli.text.state" status
        ]
        @ fields
        @ [
            line
                language
                "ui.availableCommands"
                (advice |> List.map (get "act" >> Grammar.caseVerb) |> String.concat ", ")
        ]

    let private receiptLines language tag value =
        let scope = memberValue "scope" value
        let token = get "act" scope

        let verb =
            if get "kind" scope = "CASE" then
                Grammar.caseVerb token
            else
                Grammar.kebab token

        let target =
            if get "kind" scope = "CASE" then
                get "caseReference" scope
            else
                get "targetActorId" scope

        let revision =
            if get "kind" scope = "CASE" then
                [ label language "ui.revisionLabel"; get "revision" scope ]
            else
                []

        [
            String.concat
                " "
                ([ tag; verb; target ]
                 @ revision
                 @ [ label language "authority.field.operationId.label"; get "operationId" value ])
        ]

    let private nextLines language value =
        match memberValue "nextCursor" value with
        | JsonValue.Null -> []
        | cursor -> [ line language "cli.text.next" (scalar cursor) ]

    let private historyLines language value =
        let entries =
            memberValue "entries" value
            |> items
            |> List.collect (fun entry ->
                let change =
                    if get "tag" entry = "SUMMARY" then
                        memberValue "change" entry
                    else
                        memberValue "receipt" entry

                let scope =
                    if get "tag" entry = "SUMMARY" then
                        change
                    else
                        memberValue "scope" change

                let summary =
                    [
                        get "revision" scope
                        get "act" scope |> Grammar.caseVerb
                        get "recordedAt" change
                        get "operationId" change
                    ]
                    |> String.concat "  "

                let snapshot = change |> memberValue "scope" |> memberValue "snapshot"

                let fields =
                    if get "disclosure" snapshot = "DISCLOSED" then
                        fieldLines language (memberValue "view" snapshot |> stateFields)
                        |> List.map ((+) "  ")
                    else
                        []

                summary :: fields)

        entries @ nextLines language value

    let private authorityRow value =
        match JsonValue.tryMember "principal" value with
        | Some principal ->
            [
                get "actorId" value
                get "kind" principal
                get "issuer" principal
                get "subject" principal
                get "enabled" value
            ]
            |> String.concat "  "
        | None ->
            let scope = memberValue "scope" value

            [ get "role" value; get "kind" scope; get "caseReference" scope ]
            |> String.concat "  "

    let private pageLines language value =
        let rows =
            memberValue "items" value
            |> items
            |> List.map (fun item ->
                if JsonValue.tryMember "caseReference" item |> Option.isSome then
                    let status =
                        if get "tag" item = "LIVE" then
                            if get "disposition" item = "VOIDED" then
                                "VOIDED"
                            else
                                get "status" item
                        else
                            get "tag" item

                    String.concat "  " [ get "caseReference" item; get "revision" item; status ]
                else
                    authorityRow item)

        rows @ nextLines language value

    let private reviewIdentity request review =
        let input = request |> Option.map Frames.input |> Option.defaultValue JsonValue.Null
        let act = memberValue "act" input
        let token = get "kind" act

        let verb =
            match token with
            | "SET_ENABLED" ->
                if memberValue "enabled" act = JsonValue.Boolean true then
                    "enable"
                else
                    "disable"
            | "SET_GRANT" ->
                if memberValue "active" act = JsonValue.Boolean true then
                    "grant"
                else
                    "revoke"
            | "REGISTER_ACTOR" -> "register"
            | _ -> Grammar.caseVerb token

        let target =
            if get "scope" review = "AUTHORITY" then
                let principal =
                    match JsonValue.tryMember "target" input with
                    | Some target -> target
                    | None -> memberValue "target" review |> memberValue "principal"

                [ get "kind" principal; get "issuer" principal; get "subject" principal ]
                |> String.concat " "
            else
                memberValue "after" review |> get "caseReference"

        verb + " " + target

    let private authorityChanges language before after =
        let grants state =
            memberValue "grants" state
            |> items
            |> List.map authorityRow
            |> String.concat "; "

        [
            "authority.field.enabled.label", get "enabled" before, get "enabled" after
            "cli.text.grants", grants before, grants after
        ]
        |> List.choose (fun (key, previous, next) ->
            if previous = next then
                None
            else
                Some(
                    line
                        language
                        key
                        ((if previous = "" then "—" else previous)
                         + " → "
                         + (if next = "" then "—" else next))
                ))

    let private reviewLines language request value =
        let review = memberValue "review" value
        let after = memberValue "after" review
        let before = memberValue "before" review

        let changes =
            if get "scope" review = "AUTHORITY" then
                authorityChanges language before after
            else
                FieldTable.rows
                |> List.choose (fun row ->
                    let previous =
                        before |> memberValue "state" |> stateFields |> memberValue row.Key

                    let next = after |> memberValue "state" |> stateFields |> memberValue row.Key

                    if previous = next then
                        None
                    else
                        Some(
                            line
                                language
                                ("field." + row.Key + ".label")
                                (displayed language previous + " → " + displayed language next)
                        ))

        line language "ui.reviewTitle" (reviewIdentity request review) :: changes

    let private diagnosticTargetIdentity =
        function
        | InputTarget.Field field -> "field." + BusinessField.token field + ".label"
        | InputTarget.OperationId -> "authority.field.operationId.label"
        | InputTarget.ExpectedRevision -> "authority.field.expectedRevision.label"
        | InputTarget.PaymentGroup -> "group.payment.label"
        | InputTarget.Reason -> "field.reason.label"
        | InputTarget.PrincipalIssuer -> "authority.field.issuer.label"
        | InputTarget.PrincipalSubject -> "authority.field.subject.label"
        | InputTarget.Limit -> "cli.target.limit"
        | InputTarget.Cursor -> "cli.target.cursor"

    let private diagnosticTarget language value =
        match JsonValue.tryMember "target" value with
        | Some(JsonValue.String _ as target) ->
            let admitted =
                (Codec.enumeration InputTargets.all).Read "$.target" target
                |> Result.defaultWith (fun _ -> invalidOp "Invalid admitted diagnostic target.")

            Some(label language (diagnosticTargetIdentity admitted))
        | _ -> None

    let private diagnostic language family value =
        let code = get "code" value

        let arguments =
            match memberValue "limits" value with
            | JsonValue.Object fields ->
                fields |> List.map (fun (key, value) -> key, scalar value) |> Map.ofList
            | _ -> Map.empty

        let message = MessageCatalogs.render language (family + code) arguments

        let message =
            match diagnosticTarget language value with
            | Some target -> target + ": " + message
            | None -> message

        let action =
            JsonValue.tryMember "action" value
            |> Option.map (scalar >> line language "ui.action")
            |> Option.toList

        message :: line language "cli.text.code" code :: action

    let private recoveryLines language value =
        let rows =
            memberValue "items" value
            |> items
            |> List.collect (fun item ->
                let act = get "act" item

                let prefix =
                    if get "scope" item = "CASE" then
                        "command."
                    else
                        "authority."

                let target =
                    if get "scope" item = "CASE" then
                        get "caseReference" item
                    else
                        get "targetActorId" item

                [
                    label language (prefix + act + ".label") + "  " + target
                    line language "cli.text.recoveryId" (get "recoveryId" item)
                    line language "authority.field.operationId.label" (get "operationId" item)
                    line language "cli.text.createdAt" (get "createdAt" item)
                    line language "cli.text.expiresAt" (get "expiresAt" item)
                    line
                        language
                        "cli.text.offers"
                        (memberValue "offers" item
                         |> items
                         |> List.map (fun offer ->
                             label language ("cli.text.offer." + scalar offer))
                         |> String.concat ", ")
                ])

        label language "cli.text.recoveryNotice" :: rows @ nextLines language value

    let private successful language endpoint value =
        match endpoint with
        | Some "case.get" -> caseLines language value
        | Some "case.history" -> historyLines language value
        | Some "case.list"
        | Some "authority.list"
        | Some "authority.grants" -> pageLines language value
        | Some "operation.pending" -> recoveryLines language value
        | Some "operation.forget" -> [ label language "cli.text.forgotten" ]
        | Some "operation.observe" -> receiptLines language "ACCEPTED" value
        | _ ->
            match value with
            | JsonValue.Object fields ->
                fields |> List.map (fun (key, value) -> key + ": " + scalar value)
            | _ -> [ scalar value ]

    let private resultLines language request frame =
        match frame.Result with
        | FrameResult.HostFailure failure ->
            [
                label language ("host." + failure.Code)
                line language "cli.text.code" failure.Code
            ]
        | FrameResult.LocalFailure failure ->
            let value = Frames.failureCodec.Write failure

            if JsonValue.tryMember "setting" value |> Option.isSome then
                [
                    MessageCatalogs.render
                        language
                        "cli.configurationIssue"
                        (Map.ofList [ "setting", get "setting" value; "issue", get "issue" value ])
                ]
            else
                [
                    label language ("cli.failure." + get "code" value)
                    line language "cli.text.code" (get "code" value)
                ]
        | FrameResult.Reply reply ->
            let outcome = memberValue "outcome" reply
            let data = memberValue "data" outcome

            match get "tag" outcome with
            | "SUCCEEDED" -> successful language frame.Endpoint data
            | "ACCEPTED"
            | "ALREADY_ACCEPTED" as tag -> receiptLines language tag data
            | "REVIEWED" -> reviewLines language request data
            | "REJECTED" -> diagnostic language "diagnostic." data
            | "FAILED" -> diagnostic language "fault." data
            | "OUTCOME_UNKNOWN" -> [ label language "cli.text.uncertain" ]
            | _ -> invalidOp "Unsupported admitted result."

    let bytesFor language request source =
        let frame =
            Frames.decode source
            |> Result.defaultWith (fun _ -> invalidOp "Invalid CLI reply.")

        let lines = resultLines language request frame

        let lines =
            if frame.Delivery = DeliveryState.Uncertain then
                let warning = label language "cli.text.uncertain"

                if List.contains warning lines then
                    lines
                else
                    lines @ [ warning ]
            else
                lines

        lines |> String.concat "\n" |> fun text -> Encoding.UTF8.GetBytes(text + "\n")

    let bytes language source = bytesFor language None source

/// Preserve the delivery writer's failure handling while rendering a complete reply atomically.
type internal TextReplySink(output: Stream, language: string, request: Invocation) =
    inherit Stream()
    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Flush() = output.Flush()

    override _.Write(bytes, offset, count) =
        let rendered =
            TextReply.bytesFor language (Some request) bytes[offset .. offset + count - 1]

        output.Write(rendered, 0, rendered.Length)

    override _.WriteByte value =
        if value <> byte '\n' then
            output.WriteByte value
