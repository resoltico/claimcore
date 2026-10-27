module ClaimCore.Tests.OperationCatalogTests

open ClaimCore.Cli
open ClaimCore.Protocol

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.ProtocolGeneration
open ClaimCore.TestSupport

let private find words =
    OperationCatalog.all
    |> List.find (fun entry -> CommandGrammar.words entry = words)

[<Tests>]
let tests =
    testList
        "core operation catalog"
        [
            testCase "every actor-facing act occurs exactly once" (fun () ->
                let actual =
                    OperationCatalog.mutations
                    |> List.choose _.SelectedAct
                    |> List.map ActKind.token

                let expected =
                    (CaseActKind.all |> List.map CaseActKind.token)
                    @ (AuthorityKind.all
                       |> List.filter ((<>) AuthorityKind.ProvisionOwner)
                       |> List.map AuthorityKind.token)

                Expect.equal
                    (Set.ofList actual)
                    (Set.ofList expected)
                    "All actor protocol acts; owner provisioning stays administration-only"

                Expect.equal actual.Length (List.distinct actual).Length "No duplicate act entry")
            testCase "actor endpoints are represented including derived preview" (fun () ->
                let expected =
                    Api.all
                    |> List.map Api.id
                    |> List.filter (fun id -> id <> Api.session.Token && id <> Api.logout.Token)
                    |> Set.ofList

                let actual =
                    Api.operationPreview.Token
                    :: (OperationCatalog.all
                        |> List.map (fun operation -> ActorEndpoint.token operation.Endpoint))
                    |> Set.ofList

                Expect.equal actual expected "Session bootstrap and logout belong to the host")
            testCase "typed payload declarations select only their own fields" (fun () ->
                let fields kind =
                    let operation = find [ "case"; kind ]
                    let inputs, _ = CommandGrammar.derive operation

                    inputs
                    |> List.choose (fun input ->
                        match input.Name with
                        | InteractionInput.Target(InputTarget.Field field) when
                            field <> BusinessField.CaseReference
                            ->
                            Some(BusinessField.key field)
                        | _ -> None)

                Expect.equal
                    (fields "open")
                    [
                        "incidentDate"
                        "incidentNotificationDate"
                        "incidentCountry"
                        "claimantName"
                        "insurerName"
                        "claimedAmount"
                        "claimedCurrency"
                    ]
                    "Opening is the registration group"

                Expect.equal (fields "void") [] "A reason act cannot borrow registration values")
            testCase "declared prefill has a wrong-value negative control" (fun () ->
                let operation = find [ "case"; "decide" ]
                let inputs, _ = OperationCatalog.caseInputs operation CaseActKind.Decide

                let check (inputs: OperationInputDefinition list) =
                    let date =
                        inputs
                        |> List.find (fun input ->
                            input.Name =
                                InteractionInput.Target(
                                    InputTarget.Field BusinessField.PaymentDecisionDate
                                ))

                    Expect.equal
                        date.Binding.Prefill
                        (Some [ "decision"; "paymentDecisionDate" ])
                        "Decision values prefill from the accepted decision"

                check inputs

                let wrong =
                    inputs
                    |> List.map (fun input ->
                        { input with
                            Binding = { input.Binding with Prefill = None }
                        })

                Expect.throws (fun () -> check wrong) "A wrong prefill binding is detected")
            testCase
                "new-case revision is a constant with an existing-case negative control"
                (fun () ->
                    let operation = find [ "case"; "open" ]
                    let inputs, constants = OperationCatalog.caseInputs operation CaseActKind.Open

                    let check
                        (inputs: OperationInputDefinition list)
                        (constants: BindingAssignment list)
                        =
                        Expect.isFalse
                            (inputs
                             |> List.exists (fun input ->
                                 input.Name =
                                     (InteractionInput.Target InputTarget.ExpectedRevision)))
                            "Opening has no authored revision flag"

                        Expect.isTrue
                            (constants
                             |> List.exists (fun item ->
                                 item.Path = [ "expectedRevision" ]
                                 && item.Value = BindingValue.Integer 0L))
                            "Opening fixes revision zero"

                    check inputs constants

                    let wrong =
                        constants |> List.filter (fun item -> item.Path <> [ "expectedRevision" ])

                    Expect.throws
                        (fun () -> check inputs wrong)
                        "A missing revision constant is detected")
            testCase
                "every entry and input has authored labels and meanings in all languages"
                (fun () ->
                    for language in LocalizationPolicy.languages do
                        use document =
                            JsonDocument.Parse(
                                File.ReadAllBytes(
                                    Path.Combine(
                                        RepositoryRoot.find (),
                                        "src/ClaimCore.Protocol/Localization",
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
                let grant = CommandGrammar.synopsis (find [ "authority"; "grant" ])
                Expect.stringContains grant "--principal-kind human|service" "Vocabulary choices"

                Expect.stringContains
                    grant
                    "--case-reference VALUE"
                    "Flag derives from the input name"

                for entry in OperationCatalog.all do
                    Expect.stringStarts
                        (CommandGrammar.synopsis entry)
                        (String.concat " " (CommandGrammar.words entry))
                        "Derived command words")
            testCase "correction bindings retain conditional replacement semantics" (fun () ->
                let inputs, _ = CommandGrammar.derive (find [ "case"; "correct" ])

                Expect.equal
                    inputs.Length
                    17
                    "Three control inputs and the three correction groups"

                let payment =
                    inputs
                    |> List.find (fun input ->
                        input.Name =
                            InteractionInput.Target(InputTarget.Field BusinessField.PaymentDate))

                Expect.equal
                    payment.Binding.Branches
                    [
                        {
                            Path = [ "act"; "groups"; "payment"; "kind" ]
                            Value = "REPLACE"
                        }
                    ]
                    "Payment date exists only for REPLACE")
            testCase "the published catalog contains all entries with their synopses" (fun () ->
                let value = Catalog.document
                Expect.equal value.Entries.Length 25 "Mutations and reads"

                Expect.equal
                    (Codec.encode CatalogCodecs.codec value
                     |> Codec.decode Limits.MaximumResponseBytes CatalogCodecs.codec)
                    (Ok value)
                    "Read and write preserve the derived catalog")
        ]
