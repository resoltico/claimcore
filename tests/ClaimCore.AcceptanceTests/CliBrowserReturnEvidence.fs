module internal ClaimCore.AcceptanceTests.CliBrowserReturnEvidence

open System.Text.Json

let private vocabulary =
    [
        "location", [ "issuer"; "callback"; "callback-upgraded"; "browser-error"; "other" ]
        "response", [ "none"; "ok"; "refused"; "other" ]
        "network",
        [
            "none"
            "refused"
            "reset"
            "closed"
            "empty"
            "timeout"
            "blocked"
            "tls"
            "other"
        ]
        "provider", [ "none"; "credentials"; "cookie"; "expired"; "disabled"; "other" ]
        "failure",
        [
            "timeout"
            "browser-closed"
            "page-closed"
            "page-crashed"
            "target-closed"
            "resource"
            "other"
        ]
    ]
    |> Map.ofList

let private names =
    [
        "location"
        "redirect"
        "requested"
        "response"
        "network"
        "provider"
        "failure"
    ]

let private field (root: JsonElement) (name: string) =
    let value = root.GetProperty name

    if name = "redirect" || name = "requested" then
        match value.ValueKind with
        | JsonValueKind.True -> Some(name + "=yes")
        | JsonValueKind.False -> Some(name + "=no")
        | _ -> None
    elif value.ValueKind = JsonValueKind.String then
        let token = value.GetString()

        if List.contains token vocabulary[name] then
            Some(name + "=" + token)
        else
            None
    else
        None

let decode (source: string) =
    try
        use document = JsonDocument.Parse(source, JsonDocumentOptions(MaxDepth = 4))
        let root = document.RootElement
        let supplied = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList

        if supplied.Length <> names.Length || Set.ofList supplied <> Set.ofList names then
            None
        else
            let fields = List.map (field root) names

            if List.forall Option.isSome fields then
                fields |> List.choose id |> String.concat ";" |> Some
            else
                None
    with
    | :? JsonException
    | :? System.InvalidOperationException -> None
