module ClaimCore.Tests.OperationCatalogTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Contract
open ClaimCore.ContractGeneration
open ClaimCore.TestSupport

let rec private pathExists
    (definitions: Map<string, Schema>)
    (known: Map<string list, string>)
    prefix
    (path: string list)
    schema
    =
    match schema with
    | Schema.Ref name -> pathExists definitions known prefix path definitions[name]
    | Schema.OneOf(cases, Some(tag, mapping)) ->
        match Map.tryFind (prefix @ [ tag ]) known with
        | Some value ->
            mapping
            |> List.tryFind (fst >> (=) value)
            |> Option.exists (fun (_, name) ->
                pathExists definitions known prefix path definitions[name])
        | None -> cases |> List.forall (pathExists definitions known prefix path)
    | _ when path.IsEmpty -> true
    | Schema.Object(properties, _) ->
        properties
        |> List.tryFind (fst >> (=) path.Head)
        |> Option.exists (fun (name, child) ->
            pathExists definitions known (prefix @ [ name ]) path.Tail child)
    | _ -> false

let private valid operation =
    let inputs, constants = OperationCatalog.derive operation

    let fixedValues =
        constants
        |> List.choose (fun item ->
            match item.Value with
            | BindingValue.Text value -> Some(item.Path, value)
            | _ -> None)

    let endpoint =
        Api.tryFind operation.Endpoint
        |> Option.defaultWith (fun () -> failtest "Unknown catalog endpoint")

    endpoint.Accept
        { new IEndpointVisitor<bool> with
            member _.Visit endpoint =
                inputs
                |> List.forall (fun input ->
                    let known =
                        fixedValues
                        @ (input.Binding.Branches
                           |> List.map (fun branch -> branch.Path, branch.Value))
                        |> Map.ofList

                    pathExists
                        endpoint.Input.Definitions
                        known
                        []
                        input.Binding.Path
                        endpoint.Input.Schema)
        }

let private find words =
    OperationCatalog.all
    |> List.find (fun entry -> OperationCatalog.words entry = words)

[<Tests>]
let tests =
    testList
        "core operation catalog"
        [
            testCase "every actor-facing act occurs exactly once" (fun () ->
                let actual =
                    OperationCatalog.mutations |> List.choose _.Act |> List.map ActKinds.token

                let expected =
                    (CaseActKinds.all |> List.map CaseActKinds.token)
                    @ (AuthorityKinds.all
                       |> List.filter ((<>) AuthorityKind.ProvisionOwner)
                       |> List.map AuthorityKinds.token)

                Expect.equal
                    (Set.ofList actual)
                    (Set.ofList expected)
                    "All actor protocol acts; owner provisioning stays administration-only"

                Expect.equal actual.Length (List.distinct actual).Length "No duplicate act entry")
            testCase "actor endpoints are represented including derived preview" (fun () ->
                let expected =
                    Api.all
                    |> List.map Api.id
                    |> List.filter (fun id -> id <> Api.session.Id && id <> Api.logout.Id)
                    |> Set.ofList

                let actual =
                    Api.operationPreview.Id :: (OperationCatalog.all |> List.map _.Endpoint)
                    |> Set.ofList

                Expect.equal actual expected "Session bootstrap and logout belong to the host")
            testCase "every derived input resolves in its selected request schema" (fun () ->
                for entry in OperationCatalog.all do
                    Expect.isTrue (valid entry) (String.concat " " (OperationCatalog.words entry)))
            testCase "a catalog input absent from its codec is rejected" (fun () ->
                let original = find [ "case"; "open" ]

                let bad =
                    { original with
                        Body =
                            CatalogShape.Object
                                [
                                    "unknownInput",
                                    CatalogShape.Input
                                        {
                                            Name =
                                                CatalogInputName.Technical TechnicalInput.Subject
                                            Atom = CatalogAtom.Text
                                            Argument = CatalogArgument.Named
                                            Required = true
                                            Default = None
                                            Prefill = None
                                        }
                                ]
                    }

                Expect.isTrue (valid original) "Positive control"
                Expect.isFalse (valid bad) "The missing codec member is detected")
            testCase
                "every entry and input has authored labels and meanings in all languages"
                (fun () ->
                    for language in LocalizationPolicy.languages do
                        use document =
                            JsonDocument.Parse(
                                File.ReadAllBytes(
                                    Path.Combine(
                                        RepositoryRoot.find (),
                                        "src/ClaimCore.Contract/Localization",
                                        language + ".json"
                                    )
                                )
                            )

                        let known =
                            document.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                        for entry in OperationCatalog.entries do
                            for key in
                                [
                                    yield entry.LabelId
                                    yield entry.MeaningId
                                    yield! Option.toList entry.ConfirmationId
                                    for input in entry.Inputs do
                                        yield input.LabelId
                                        yield! Option.toList input.MeaningId
                                ] do
                                Expect.isTrue (Set.contains key known) (language + ": " + key))
            testCase "synopses derive choices and flag names without exceptions" (fun () ->
                let grant = OperationCatalog.synopsis (find [ "authority"; "grant" ])
                Expect.stringContains grant "--kind human|service" "Vocabulary choices"

                Expect.stringContains
                    grant
                    "--case-reference VALUE"
                    "Flag derives from the input name"

                Expect.isFalse
                    (grant.Contains("--case VALUE", StringComparison.Ordinal))
                    "No retired flag alias"

                for entry in OperationCatalog.all do
                    Expect.stringStarts
                        (OperationCatalog.synopsis entry)
                        (String.concat " " (OperationCatalog.words entry))
                        "Derived command words")
            testCase "correction bindings retain conditional replacement semantics" (fun () ->
                let inputs, _ = OperationCatalog.derive (find [ "case"; "correct" ])

                Expect.equal
                    inputs.Length
                    17
                    "Three control inputs and the three correction groups"

                let payment =
                    inputs
                    |> List.find (fun input ->
                        input.Spec.Name = CatalogInputName.Field BusinessField.PaymentDate)

                Expect.equal
                    payment.Binding.Branches
                    [
                        {
                            Path = [ "act"; "groups"; "payment"; "mode" ]
                            Value = "REPLACE"
                        }
                    ]
                    "Payment date exists only for REPLACE")
            testCase "the published catalog contains all entries with their synopses" (fun () ->
                let value = ContractCatalog.valueFrom ContractCatalog.defaults
                Expect.equal value.Entries.Length 25 "Mutations and reads"

                Expect.equal
                    (Codec.encode CatalogCodecs.codec value
                     |> Codec.decode Limits.MaximumResponseBytes CatalogCodecs.codec)
                    (Ok value)
                    "Read and write preserve the derived catalog")
        ]
