namespace ClaimCore.ContractGeneration

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ClaimCore.Catalog
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Wire

/// Fixture-only core/codec evolution; never an offered production operation.
module EvolutionQualification =
    type private RegistrationFixture =
        {
            Registration: Registration
            Reason: string
        }

    let private registration =
        (Codec.record (fun a b c d e f g reason ->
            {
                Registration =
                    {
                        IncidentDate = a
                        IncidentNotificationDate = b
                        IncidentCountry = c
                        ClaimantName = d
                        InsurerName = e
                        ClaimedAmount = f
                        ClaimedCurrency = g
                    }
                Reason = reason
            })
        : Fields<RegistrationFixture, _>)
        |> Codec.field "incidentDate" _.Registration.IncidentDate Codec.text
        |> Codec.field "incidentNotificationDate" _.Registration.IncidentNotificationDate Codec.text
        |> Codec.field "incidentCountry" _.Registration.IncidentCountry Codec.text
        |> Codec.field "claimantName" _.Registration.ClaimantName Codec.text
        |> Codec.field "insurerName" _.Registration.InsurerName Codec.text
        |> Codec.field "claimedAmount" _.Registration.ClaimedAmount Codec.text
        |> Codec.field "claimedCurrency" _.Registration.ClaimedCurrency Codec.text
        |> Codec.field "reason" _.Reason Codec.text
        |> Codec.build
        |> Codec.named "Registration"

    let private act =
        Codec.tagged
            "CaseAct"
            "kind"
            [
                Codec.variant
                    "OPEN"
                    id
                    Some
                    (Codec.record id |> Codec.field "values" id registration)
            ]

    let private request =
        Codec.record (fun operation reference revision act -> operation, reference, revision, act)
        |> Codec.field "operationId" (fun (operation, _, _, _) -> operation) Codec.uuid
        |> Codec.field "caseReference" (fun (_, reference, _, _) -> reference) Codec.text
        |> Codec.field
            "expectedRevision"
            (fun (_, _, revision, _) -> revision)
            RequestCodecs.revision
        |> Codec.field "act" (fun (_, _, _, act) -> act) act
        |> Codec.build
        |> Codec.named "EvolutionRequest"

    let private group (group: CorrectionGroupDefinition) =
        if group.Name = CorrectionGroup.Registration then
            { group with
                LabelId = "group.payment.label"
                MeaningId = "group.payment.meaning"
            }
        else
            group

    let private command (definition: CommandDefinition) =
        if definition.Kind = CaseActKind.Business BusinessActKind.Open then
            let fields =
                match definition.Inputs with
                | CommandInputShape.Fields fields -> fields
                | _ -> invalidOp "Expected flat fixture inputs."

            { definition with
                LabelId = "command.CLOSE.label"
                MeaningId = "command.CLOSE.meaning"
                ConfirmationId = "ui.confirmExactRequest"
                Inputs =
                    CommandInputShape.Fields(
                        fields
                        @ [
                            {
                                FieldName = InputField.Reason
                                Prefill = PrefillSource.Blank
                            }
                        ]
                    )
            }
        else
            match definition.Inputs with
            | CommandInputShape.CorrectionGroups groups ->
                { definition with
                    Inputs = CommandInputShape.CorrectionGroups(List.map group groups)
                }
            | _ -> definition

    let private presentation () =
        let defaults = ContractDescriptors.defaults

        { defaults with
            Fields =
                defaults.Fields
                |> List.map (fun field ->
                    if field.Name = BusinessField.IncidentDate then
                        { field with
                            LabelId = "field.paymentDate.label"
                            MeaningId = "field.paymentDate.meaning"
                        }
                    else
                        field)
            Commands = defaults.Commands |> List.map command
            AuthorityTarget =
                { defaults.AuthorityTarget with
                    LabelId = "authority.field.subject.label"
                }
            AuthorityCommands =
                defaults.AuthorityCommands
                |> List.map (fun operation ->
                    if operation.Kind = AuthorityKind.SetGrant then
                        { operation with
                            LabelId = "authority.REGISTER_ACTOR.label"
                            MeaningId = "authority.REGISTER_ACTOR.meaning"
                            Inputs =
                                operation.Inputs
                                |> List.map (fun input ->
                                    if input.Name = "role" then
                                        { input with
                                            LabelId = "authority.field.scope.label"
                                        }
                                    else
                                        input)
                        }
                    else
                        operation)
        }

    let private schemaNode schema =
        use output = new MemoryStream()
        use writer = new Utf8JsonWriter(output)
        Codec.writeSchema "#/components/schemas/" writer schema
        writer.Flush()
        JsonNode.Parse(output.ToArray()) |> nonNull

    let private document () =
        let root = schemaNode request.Schema
        let definitions = JsonObject()

        for KeyValue(name, schema) in request.Definitions do
            definitions[name] <- schemaNode schema

        let components = JsonObject()
        components["schemas"] <- definitions
        root["components"] <- components
        root

    let render () =
        let definitions = presentation ()

        let opening =
            definitions.Commands
            |> List.filter (fun command -> command.Kind = CaseActKind.Business BusinessActKind.Open)

        let value =
            {
                Registration =
                    {
                        IncidentDate = "2026-08-01"
                        IncidentNotificationDate = "2026-08-02"
                        IncidentCountry = "LV"
                        ClaimantName = "Synthetic claimant"
                        InsurerName = "Synthetic insurer"
                        ClaimedAmount = "1.00"
                        ClaimedCurrency = "EUR"
                    }
                Reason = "Synthetic evolved input"
            }

        let authored =
            Guid.Parse("10000000-0000-4000-8000-000000000001"), "SYNTHETIC-EVOLUTION", 0L, value

        let bytes = Codec.encode request authored

        match Codec.decode 65536 request bytes with
        | Error _ -> invalidOp "Evolved fixture bytes were not admitted by their typed codec."
        | Ok decoded when decoded <> authored -> invalidOp "Evolved fixture did not round trip."
        | Ok _ -> ()

        let root = JsonObject()
        root["descriptors"] <- JsonNode.Parse(ContractDescriptors.renderFrom definitions)
        root["bindings"] <- JsonNode.Parse(RequestBindings.renderFrom request.Definitions opening)
        root["schema"] <- document ()
        root["request"] <- JsonNode.Parse bytes
        root["requestBytes"] <- JsonValue.Create(Encoding.UTF8.GetString bytes)
        Encoding.UTF8.GetBytes(root.ToJsonString() + "\n")
