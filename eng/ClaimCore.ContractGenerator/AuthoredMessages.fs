namespace ClaimCore.ContractGeneration

open System
open System.Reflection
open System.Text.Json
open ClaimCore.Contract

/// Publication admits authored resources with the same pure grammar as consuming hosts.
module AuthoredMessages =
    let private entries (assembly: Assembly) name =
        use stream = assembly.GetManifestResourceStream(name) |> nonNull
        use document = JsonDocument.Parse stream

        document.RootElement.EnumerateObject()
        |> Seq.map (fun item -> item.Name, item.Value.GetString() |> nonNull)
        |> Seq.toList

    let private catalogs =
        lazy
            let assembly = Assembly.GetExecutingAssembly()

            assembly.GetManifestResourceNames()
            |> Array.filter (fun name ->
                name.StartsWith("ClaimCore.Localization.", StringComparison.Ordinal))
            |> Array.groupBy (fun name -> name.Split('.')[2])
            |> Array.map (fun (language, names) ->
                let catalog =
                    names |> Array.toList |> List.collect (entries assembly) |> Messages.catalog

                match catalog with
                | Ok value -> language, value
                | Error error -> invalidOp error)
            |> Map.ofArray

    let private catalog language = Map.find language catalogs.Value

    let render language key arguments =
        Messages.render (catalog language) key arguments

    let templates language = catalog language |> Messages.templates

    let literal key = render "en" key Map.empty

    let private bounds =
        Map.ofList
            [
                "maximumInvocationBytes", string Limits.MaximumInvocationBytes
                "maximumRequestBytes", string Limits.MaximumRequestBytes
                "maximumResponseBytes", string Limits.MaximumResponseBytes
                "maximumSecretBytes", string CliConfigurationLimits.MaximumSecretBytes
                "maximumTrustRootBytes", string CliConfigurationLimits.MaximumTrustRootBytes
            ]

    let private helpBody language key =
        let required =
            catalog language |> Messages.templates |> Map.find key |> Template.arguments

        let supplied =
            required |> Seq.map (fun name -> name, Map.find name bounds) |> Map.ofSeq

        render language key supplied

    let help language (topic: string) =
        if topic.StartsWith("usage.", StringComparison.Ordinal) then
            render language ("cli." + topic) Map.empty
        else
            let topic =
                if topic.StartsWith("diagnostic.", StringComparison.Ordinal) then
                    topic.Substring "diagnostic.".Length
                else
                    topic

            let body = helpBody language ("cli.help." + topic)
            render language "cli.help.layout" (Map.ofList [ "body", body ])
