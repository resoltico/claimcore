module ClaimCore.Tests.RecordPropertyTests

open System
open System.Buffers
open System.Globalization
open System.Text.Json
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.PropertyHarness

let private safeText =
    Gen.choice
        [
            Gen.string (Range.constant 1 20) Gen.alpha
            Gen.item [ "Žąsis"; "東京"; "😀"; "e\u0301"; "quoted\"value"; "path\\value" ]
        ]

let private registrationGenerator =
    gen {
        let! country = safeText
        let! claimant = safeText
        let! insurer = safeText
        let! amount = Gen.uint32 (Range.constant 0u 1_000_000u)

        return
            {
                IncidentDate = "2026-08-01"
                IncidentNotificationDate = "2026-08-03"
                IncidentCountry = country
                ClaimantName = claimant
                InsurerName = insurer
                ClaimedAmount = amount.ToString(CultureInfo.InvariantCulture)
                ClaimedCurrency = "EUR"
            }
    }

let private commandsGenerator =
    gen {
        let! registration = registrationGenerator
        let! payable = Gen.uint32 (Range.constant 0u 1_000_000u)

        let decision =
            {
                PaymentDecisionDate = "2026-08-15"
                PayableAmount = payable.ToString(CultureInfo.InvariantCulture)
                PayableCurrency = "USD"
            }

        let corrections =
            [ RegistrationCorrection.Keep; RegistrationCorrection.Replace registration ]
            |> List.collect (fun registration ->
                [
                    DecisionCorrection.Keep
                    DecisionCorrection.Replace decision
                    DecisionCorrection.Clear
                ]
                |> List.collect (fun decision ->
                    [
                        PaymentCorrection.Keep
                        PaymentCorrection.Replace "2026-08-20"
                        PaymentCorrection.Clear
                    ]
                    |> List.map (fun payment ->
                        Command.CorrectCase
                            {
                                Registration = registration
                                Decision = decision
                                Payment = payment
                            })))

        return
            [
                Command.Open registration
                Command.AmendRegistration registration
                Command.Decide decision
                Command.WithdrawDecision
                Command.RecordPayment "2026-08-20"
                Command.ClearPayment
                Command.Close
                Command.Reopen
            ]
            @ corrections
    }

let private requestsGenerator =
    gen {
        let! operationId = Gen.guid
        let! reference = safeText
        let! version = Gen.int64 (Range.constant 0L 1_000_000L)
        let! commands = commandsGenerator

        return
            commands
            |> List.map (fun command ->
                {
                    OperationId =
                        if operationId = Guid.Empty then
                            Guid.Parse("20000000-0000-4000-8000-000000000001")
                        else
                            operationId
                    CaseReference = reference
                    ExpectedVersion = version
                    Command = command
                })
    }

let private limit = 65536

let private correctionModesMatch (request: CommandRequest) (encoded: byte array) =
    match request.Command with
    | Command.CorrectCase correction ->
        let registration =
            match correction.Registration with
            | RegistrationCorrection.Keep -> "KEEP"
            | RegistrationCorrection.Replace _ -> "REPLACE"

        let decision =
            match correction.Decision with
            | DecisionCorrection.Keep -> "KEEP"
            | DecisionCorrection.Replace _ -> "REPLACE"
            | DecisionCorrection.Clear -> "CLEAR"

        let payment =
            match correction.Payment with
            | PaymentCorrection.Keep -> "KEEP"
            | PaymentCorrection.Replace _ -> "REPLACE"
            | PaymentCorrection.Clear -> "CLEAR"

        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(encoded))
        let command = document.RootElement.GetProperty("command")

        [ "registration", registration; "decision", decision; "payment", payment ]
        |> List.forall (fun (group, expected) ->
            command.GetProperty(group).GetProperty("mode").GetString() = expected)
    | _ -> true

let private requestRoundTripProperty =
    property {
        let! requests = requestsGenerator

        return
            requests
            |> List.forall (fun request ->
                let encoded = RequestRecord.encode request

                RequestRecord.decode limit encoded = Ok request
                && RequestRecord.encode request = encoded
                && correctionModesMatch request encoded)
    }

let private reordered (bytes: byte array) =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    let buffer = ArrayBufferWriter<byte>()

    use writer =
        new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = true, SkipValidation = false))

    writer.WriteStartObject()

    for name in
        [
            "command"
            "expectedVersion"
            "caseReference"
            "operationId"
            "canonicalCommandFormat"
        ] do
        writer.WritePropertyName(name)
        document.RootElement.GetProperty(name).WriteTo(writer)

    writer.WriteEndObject()
    writer.Flush()
    buffer.WrittenSpan.ToArray()

let private fingerprint request =
    match Operation.prepare request with
    | Ok operation -> Operation.fingerprint operation
    | Error _ -> failtest "Generated request must be domain-valid."

let private identityChanges (request: CommandRequest) =
    let replacementId = Guid.Parse("20000000-0000-4000-8000-000000000099")

    let distinctId =
        if request.OperationId = replacementId then
            Guid.Parse("20000000-0000-4000-8000-000000000098")
        else
            replacementId

    let differentCommand =
        match request.Command with
        | Command.Close -> Command.Reopen
        | _ -> Command.Close

    let changed =
        [
            { request with
                CaseReference = request.CaseReference + "X"
            }
            { request with
                OperationId = distinctId
            }
            { request with
                ExpectedVersion = request.ExpectedVersion + 1L
            }
            { request with
                Command = differentCommand
            }
        ]

    changed

let private identityMatches request =
    match request.Command with
    | Command.CorrectCase {
                              Registration = RegistrationCorrection.Keep
                              Decision = DecisionCorrection.Keep
                              Payment = PaymentCorrection.Keep
                          } ->
        match Operation.prepare request with
        | Error DomainError.CorrectionNoChanges -> true
        | _ -> false
    | _ ->
        identityChanges request
        |> List.forall (fun changed -> fingerprint changed <> fingerprint request)

let private fingerprintProperty =
    property {
        let! requests = requestsGenerator

        return
            requests
            |> List.forall (fun request ->
                let decoded =
                    request |> RequestRecord.encode |> reordered |> RequestRecord.decode limit

                match decoded with
                | Ok value -> value = request && identityMatches request
                | Error _ -> false)
    }

let tests =
    testList
        "Hedgehog canonical records"
        [
            testCase "all command and correction group shapes retain external meaning" (fun () ->
                run "CC-PROP-RECORD-001" requestRoundTripProperty)
            testCase "layout is insignificant but semantic changes affect identity" (fun () ->
                run "CC-PROP-FINGERPRINT-001" fingerprintProperty)
        ]
