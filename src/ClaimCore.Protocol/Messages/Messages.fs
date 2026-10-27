namespace ClaimCore.Protocol

type MessageCatalog = private MessageCatalog of Map<string, Template>

/// Hosts admit authored JSON once; rendering performs no I/O or locale lookup.
module Messages =
    let catalog entries =
        let rec collect result =
            function
            | [] -> Ok(MessageCatalog result)
            | (key, _) :: _ when Map.containsKey key result -> Error "Duplicate message key."
            | (key, text) :: remaining ->
                Template.parse text
                |> Result.bind (fun template -> collect (Map.add key template result) remaining)

        collect Map.empty entries

    let templates (MessageCatalog catalog) = catalog

    let render (MessageCatalog catalog) key arguments =
        match Map.tryFind key catalog with
        | Some template -> Template.render template arguments
        | None -> invalidArg (nameof key) "Message key is absent from the catalog."
