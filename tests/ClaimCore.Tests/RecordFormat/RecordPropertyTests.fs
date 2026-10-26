module ClaimCore.Tests.RecordPropertyTests

open ClaimCore.TestSupport

open System
open System.Buffers
open System.Globalization
open System.Text.Json
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.PropertyHarness

let private safeText =
    Gen.choice
        [
            Gen.string (Range.constant 1 20) Gen.alpha
            Gen.item [ "Žąsis"; "東京"; "😀"; "é"; "quoted\"value"; "path\\value" ]
        ]

let private registrationGenerator =
    gen {
        let! country = Gen.item [ "LV"; "ZZ"; "QQ" ]
        let! claimant = safeText
        let! insurer = safeText
        let! amount = Gen.uint32 (Range.constant 0u 1_000_000u)

        return
            {
                RegistrationInput.IncidentDate = "2026-08-01"
                IncidentNotificationDate = "2026-08-03"
                IncidentCountry = country
                ClaimantName = claimant
                InsurerName = insurer
                ClaimedAmount = amount.ToString(CultureInfo.InvariantCulture)
                ClaimedCurrency = "EUR"
            }
    }

let private actsGenerator =
    gen {
        let! registration = registrationGenerator
        let! payable = Gen.uint32 (Range.constant 0u 1_000_000u)

        let decision =
            {
                DecisionInput.PaymentDecisionDate = "2026-08-15"
                PayableAmount = payable.ToString(CultureInfo.InvariantCulture)
                PayableCurrency = "USD"
            }

        let corrections =
            [ RegistrationGroup.Keep; RegistrationGroup.Replace registration ]
            |> List.collect (fun registration ->
                [ DecisionGroup.Keep; DecisionGroup.Replace decision; DecisionGroup.Clear ]
                |> List.collect (fun decision ->
                    [
                        PaymentGroup.Keep
                        PaymentGroup.Replace { PaymentDate = "2026-08-20" }
                        PaymentGroup.Clear
                    ]
                    |> List.map (fun payment ->
                        UnvalidatedCaseAct.Correct
                            {
                                Registration = registration
                                Decision = decision
                                Payment = payment
                            })))

        return
            [
                UnvalidatedCaseAct.Open registration
                UnvalidatedCaseAct.AmendRegistration registration
                UnvalidatedCaseAct.Decide decision
                UnvalidatedCaseAct.WithdrawDecision
                UnvalidatedCaseAct.RecordPayment { PaymentDate = "2026-08-20" }
                UnvalidatedCaseAct.ClearPayment
                UnvalidatedCaseAct.Close
                UnvalidatedCaseAct.Reopen
            ]
            @ corrections
    }

let private requestsGenerator =
    gen {
        let! operationId = Gen.guid
        let! reference = Gen.string (Range.constant 1 20) Gen.alphaNum
        let! version = Gen.int64 (Range.constant 0L 1_000_000L)
        let! acts = actsGenerator

        return
            acts
            |> List.map (fun act ->
                {
                    OperationId =
                        if operationId = Guid.Empty then
                            Guid.Parse("20000000-0000-4000-8000-000000000001")
                        else
                            operationId
                    CaseReference = reference
                    ExpectedRevision = version
                    Act = act
                })
    }

let private limit = 65536

let private correctionKindsMatch (request: UnvalidatedCaseRequest) (encoded: byte array) =
    match request.Act with
    | (UnvalidatedCaseAct.Correct correction) ->
        let registration =
            match correction.Registration with
            | RegistrationGroup.Keep -> "KEEP"
            | RegistrationGroup.Replace _ -> "REPLACE"

        let decision =
            match correction.Decision with
            | DecisionGroup.Keep -> "KEEP"
            | DecisionGroup.Replace _ -> "REPLACE"
            | DecisionGroup.Clear -> "CLEAR"

        let payment =
            match correction.Payment with
            | PaymentGroup.Keep -> "KEEP"
            | PaymentGroup.Replace _ -> "REPLACE"
            | PaymentGroup.Clear -> "CLEAR"

        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(encoded))
        let groups = document.RootElement.GetProperty("act").GetProperty("groups")

        [ "registration", registration; "decision", decision; "payment", payment ]
        |> List.forall (fun (group, expected) ->
            groups.GetProperty(group).GetProperty("kind").GetString() = expected)
    | _ -> true

let private requestRoundTripProperty =
    property {
        let! requests = requestsGenerator

        return
            requests
            |> List.forall (fun request ->
                let encoded = CaseFixtures.encode request

                RequestRecord.decode limit encoded = Ok(CaseFixtures.command request)
                && CaseFixtures.encode request = encoded
                && correctionKindsMatch request encoded)
    }

let private reordered (bytes: byte array) =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    let buffer = ArrayBufferWriter<byte>()

    use writer =
        new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = true, SkipValidation = false))

    writer.WriteStartObject()

    for property in document.RootElement.EnumerateObject() |> Seq.rev do
        writer.WritePropertyName(property.Name)
        property.Value.WriteTo(writer)

    writer.WriteEndObject()
    writer.Flush()
    buffer.WrittenSpan.ToArray()

let private fingerprint request =
    CaseFixtures.validate request
    |> Result.defaultWith (fun _ -> failtest "Generated request must be domain-valid.")

    request
    |> CaseFixtures.encode
    |> LedgerEntry.requestDigest
    |> Convert.ToHexStringLower

let private identityChanges (request: UnvalidatedCaseRequest) =
    let replacementId = Guid.Parse("20000000-0000-4000-8000-000000000099")

    let distinctId =
        if request.OperationId = replacementId then
            Guid.Parse("20000000-0000-4000-8000-000000000098")
        else
            replacementId

    let differentAct =
        match request.Act with
        | UnvalidatedCaseAct.Close -> UnvalidatedCaseAct.Reopen
        | _ -> UnvalidatedCaseAct.Close

    let changed =
        [
            { request with
                CaseReference = request.CaseReference + "X"
            }
            { request with
                OperationId = distinctId
            }
            { request with
                ExpectedRevision = request.ExpectedRevision + 1L
            }
            { request with Act = differentAct }
        ]

    changed

let private identityMatches request =
    let original = fingerprint request

    identityChanges request
    |> List.forall (fun changed -> fingerprint changed <> original)

let private fingerprintProperty =
    property {
        let! requests = requestsGenerator

        return
            requests
            |> List.forall (fun request ->
                let decoded =
                    request |> CaseFixtures.encode |> reordered |> RequestRecord.decode limit

                match decoded with
                | Ok value -> value.Authored = request && identityMatches request
                | Error _ -> false)
    }

[<Tests>]
let tests =
    testList
        "Hedgehog canonical records"
        [
            testCase "all act and correction group shapes retain external meaning" (fun () ->
                run "CC-PROP-RECORD-001" requestRoundTripProperty)
            testCase "layout is insignificant but semantic changes affect identity" (fun () ->
                run "CC-PROP-FINGERPRINT-001" fingerprintProperty)
        ]
