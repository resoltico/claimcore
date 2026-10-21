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
                Role = ArtifactRole.RuntimePrecursor
                Bytes = OpenApi.emit ()
            }
            {
                Name = "correlation-policy.json"
                Role = ArtifactRole.RuntimePrecursor
                Bytes = DeliveryTables.correlation ()
            }
            {
                Name = "delivery-policy.json"
                Role = ArtifactRole.RuntimePrecursor
                Bytes = DeliveryTables.render ()
            }
            {
                Name = "request-bindings.json"
                Role = ArtifactRole.RuntimePrecursor
                Bytes = RequestBindings.render ()
            }
            {
                Name = "descriptors.json"
                Role = ArtifactRole.RuntimePrecursor
                Bytes = ContractDocuments.descriptors ()
            }
            {
                Name = "cli-frame.schema.json"
                Role = ArtifactRole.RuntimePrecursor
                Bytes = ContractDocuments.frames ()
            }
        ]

    let private qualification () =
        [
            {
                Name = "delivery-vectors.json"
                Role = ArtifactRole.QualificationOrTooling
                Bytes = oracle "delivery-vectors.json" |> PolicyVectors.delivery
            }
            {
                Name = "correlation-vectors.json"
                Role = ArtifactRole.QualificationOrTooling
                Bytes = oracle "correlation-vectors.json" |> PolicyVectors.correlation
            }
            {
                Name = "corpus.json"
                Role = ArtifactRole.QualificationOrTooling
                Bytes = SchemaSamples.corpus ()
            }
            {
                Name = "package.json"
                Role = ArtifactRole.QualificationOrTooling
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
                Role = ArtifactRole.RuntimePrecursor
                Bytes = output.ToArray()
            })
        |> Array.toList

    let private helpDocument () =
        [
            for language in ClaimCore.Catalog.LocalizationPolicy.languages do
                for topic in ClaimCore.Catalog.CliHelp.topics do
                    language + "." + topic,
                    ClaimCore.Catalog.CliHelp.text
                        language
                        topic
                        Limits.MaximumInvocationBytes
                        Limits.MaximumRequestBytes
                        Limits.MaximumResponseBytes
        ]
        @ [
            for language in ClaimCore.Catalog.LocalizationPolicy.languages do
                for issue in
                    [
                        ClaimCore.Catalog.CliUsageIssue.Invocation
                        ClaimCore.Catalog.CliUsageIssue.Topic
                        ClaimCore.Catalog.CliUsageIssue.Options
                        ClaimCore.Catalog.CliUsageIssue.Selector
                    ] do
                    language + ".usage." + string issue,
                    ClaimCore.Catalog.CliHelp.usage language issue
        ]
        @ [
            for language in ClaimCore.Catalog.LocalizationPolicy.languages do
                for id in ClaimCore.Catalog.CliHelp.diagnosticTopics do
                    language + ".diagnostic." + id,
                    ClaimCore.Catalog.CliHelp.diagnosticDiscovery
                        language
                        id
                        Limits.MaximumInvocationBytes
                        Limits.MaximumRequestBytes
                        Limits.MaximumResponseBytes
        ]
        |> Map.ofList
        |> System.Text.Json.JsonSerializer.SerializeToUtf8Bytes

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
                Role = ArtifactRole.RuntimePrecursor
                Bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes policy
            }
            {
                Name = "cli-help.json"
                Role = ArtifactRole.RuntimePrecursor
                Bytes = helpDocument ()
            }
            {
                Name = "cli-messages.json"
                Role = ArtifactRole.RuntimePrecursor
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
        let inputs = authoritative () @ qualification () @ locales () @ presentation ()
        let manifest = ContractIdentity.manifest inputs
        let identity = ContractIdentity.digest manifest

        inputs
        @ [
            {
                Name = "evolution-fixture.json"
                Role = ArtifactRole.QualificationOrTooling
                Bytes = EvolutionQualification.render ()
            }
            {
                Name = "contract-manifest.json"
                Role = ArtifactRole.IdentityDerived
                Bytes = manifest
            }
            {
                Name = "contract-identity.txt"
                Role = ArtifactRole.IdentityDerived
                Bytes = Encoding.UTF8.GetBytes(identity + "\n")
            }
            {
                Name = "contract-identity.fs"
                Role = ArtifactRole.IdentityDerived
                Bytes =
                    Encoding.UTF8.GetBytes(
                        "namespace ClaimCore.Wire\n\nmodule internal ConsumerContractIdentity =\n    let Value = \""
                        + identity
                        + "\"\n"
                    )
            }
            {
                Name = "client.ts"
                Role = ArtifactRole.IdentityDerived
                Bytes = TypeScript.source identity
            }
        ]
