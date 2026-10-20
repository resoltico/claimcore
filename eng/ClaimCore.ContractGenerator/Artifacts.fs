namespace ClaimCore.ContractGeneration

open System.IO
open System.Reflection
open System.Text
open ClaimCore.Wire

module ContractArtifacts =
    let private oracle name =
        use stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaimCore.Oracles." + name)
            |> nonNull

        use output = new MemoryStream()
        stream.CopyTo output
        output.ToArray()

    let private authoritative () =
        [
            {
                Name = "openapi.json"
                Bytes = OpenApi.emit ()
            }
            {
                Name = "correlation-policy.json"
                Bytes = DeliveryTables.correlation ()
            }
            {
                Name = "delivery-policy.json"
                Bytes = DeliveryTables.render ()
            }
            {
                Name = "request-bindings.json"
                Bytes = RequestBindings.render ()
            }
            {
                Name = "descriptors.json"
                Bytes = ContractDocuments.descriptors ()
            }
            {
                Name = "cli-frame.schema.json"
                Bytes = ContractDocuments.frames ()
            }
            {
                Name = "delivery-vectors.json"
                Bytes = oracle "delivery-vectors.json" |> PolicyVectors.delivery
            }
            {
                Name = "correlation-vectors.json"
                Bytes = oracle "correlation-vectors.json" |> PolicyVectors.correlation
            }
            {
                Name = "corpus.json"
                Bytes = SchemaSamples.corpus ()
            }
            {
                Name = "package.json"
                Bytes = Encoding.UTF8.GetBytes "{\"private\":true,\"type\":\"module\"}\n"
            }
        ]

    let private locales () =
        let assembly = Assembly.GetExecutingAssembly()

        assembly.GetManifestResourceNames()
        |> Array.filter (fun name ->
            name.StartsWith("ClaimCore.Localization.", System.StringComparison.Ordinal))
        |> Array.sort
        |> Array.map (fun name ->
            use stream = assembly.GetManifestResourceStream(name) |> nonNull
            use output = new MemoryStream()
            stream.CopyTo output

            {
                Name = "locale." + name.Substring("ClaimCore.Localization.".Length)
                Bytes = output.ToArray()
            })
        |> Array.toList

    let private presentation () =
        let policy =
            {|
                languageOptions = ClaimCore.Catalog.LocalizationPolicy.languageOptions
                displayOptions = ClaimCore.Catalog.LocalizationPolicy.displayOptions
                languages = ClaimCore.Catalog.LocalizationPolicy.languages
                pseudolanguage = ClaimCore.Catalog.LocalizationPolicy.pseudolanguage
                displayLocales = ClaimCore.Catalog.LocalizationPolicy.displayLocales
                defaultLanguage = ClaimCore.Catalog.LocalizationPolicy.defaultLanguage
                defaultDisplayLocale = ClaimCore.Catalog.LocalizationPolicy.defaultDisplayLocale
            |}

        [
            {
                Name = "localization-policy.json"
                Bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes policy
            }
            {
                Name = "cli-messages.json"
                Bytes =
                    ClaimCore.Catalog.MessageText.all
                    |> Map.filter (fun key _ ->
                        key.StartsWith("cli.", System.StringComparison.Ordinal))
                    |> Map.map (fun key _ -> ClaimCore.Catalog.MessageText.literal key)
                    |> System.Text.Json.JsonSerializer.SerializeToUtf8Bytes
            }
        ]

    let all () =
        CatalogValidation.validate ()
        CorrelationValidation.validate CorrelationBindings.all
        let inputs = authoritative () @ locales () @ presentation ()
        let manifest = ContractIdentity.manifest inputs
        let identity = ContractIdentity.digest manifest

        inputs
        @ [
            {
                Name = "contract-manifest.json"
                Bytes = manifest
            }
            {
                Name = "contract-identity.txt"
                Bytes = Encoding.UTF8.GetBytes(identity + "\n")
            }
            {
                Name = "contract-identity.fs"
                Bytes =
                    Encoding.UTF8.GetBytes(
                        "namespace ClaimCore.Wire\n\nmodule internal ConsumerContractIdentity =\n    let Value = \""
                        + identity
                        + "\"\n"
                    )
            }
            {
                Name = "client.ts"
                Bytes = TypeScript.source identity
            }
        ]
