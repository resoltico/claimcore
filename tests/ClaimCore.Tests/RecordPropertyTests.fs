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

let private safeText = Gen.string (Range.constant 1 20) Gen.alpha

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

let private commandGenerator =
    gen {
        let! registration = registrationGenerator
        let! payable = Gen.uint32 (Range.constant 0u 1_000_000u)

        let correction =
            Command.CorrectCase
                {
                    Registration = RegistrationCorrection.Replace registration
                    Decision =
                        DecisionCorrection.Replace
                            {
                                PaymentDecisionDate = "2026-08-15"
                                PayableAmount = payable.ToString(CultureInfo.InvariantCulture)
                                PayableCurrency = "USD"
                            }
                    Payment = PaymentCorrection.Clear
                }

        return!
            Gen.item
                [
                    Command.Open registration
                    Command.AmendRegistration registration
                    correction
                    Command.Decide
                        {
                            PaymentDecisionDate = "2026-08-15"
                            PayableAmount = payable.ToString(CultureInfo.InvariantCulture)
                            PayableCurrency = "USD"
                        }
                    Command.WithdrawDecision
                    Command.RecordPayment "2026-08-20"
                    Command.ClearPayment
                    Command.Close
                    Command.Reopen
                ]
    }

let private requestGenerator =
    gen {
        let! operationId = Gen.guid
        let! reference = safeText
        let! version = Gen.int64 (Range.constant 0L 1_000_000L)
        let! command = commandGenerator

        return
            {
                OperationId =
                    if operationId = Guid.Empty then
                        Guid.Parse("20000000-0000-4000-8000-000000000001")
                    else
                        operationId
                CaseReference = reference
                ExpectedVersion = version
                Command = command
            }
    }

let private limit = 65536

let private requestRoundTripProperty =
    property {
        let! request = requestGenerator
        let encoded = RequestRecord.encode request

        return
            RequestRecord.decode limit encoded = Ok request
            && RequestRecord.encode request = encoded
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
            "protocolVersion"
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

let private fingerprintProperty =
    property {
        let! request = requestGenerator

        let decoded =
            request |> RequestRecord.encode |> reordered |> RequestRecord.decode limit

        let changed =
            { request with
                CaseReference = request.CaseReference + "X"
            }

        return
            match decoded with
            | Ok value ->
                value = request
                && fingerprint value = fingerprint request
                && fingerprint changed <> fingerprint request
            | Error _ -> false
    }

let tests =
    testList
        "Hedgehog canonical records"
        [
            testCase "all command shapes retain external meaning" (fun () ->
                run "CC-PROP-RECORD-001" requestRoundTripProperty)
            testCase "layout is insignificant but semantic changes affect identity" (fun () ->
                run "CC-PROP-FINGERPRINT-001" fingerprintProperty)
        ]
