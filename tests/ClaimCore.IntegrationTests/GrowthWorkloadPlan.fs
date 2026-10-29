module internal ClaimCore.IntegrationTests.GrowthWorkloadPlan

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Protocol
open ClaimCore.TestSupport

type Workload =
    {
        Writes: int
        Cases: int
        Actors: int
        Writers: int
    }

let comparison =
    match Environment.GetEnvironmentVariable "CLAIMCORE_GROWTH_COMPARISON" with
    | null -> false
    | "1" -> true
    | _ -> invalidOp "CLAIMCORE_GROWTH_COMPARISON must be unset or 1."

let comparedWrites = [ 10000, 1; 100000, 1; 100000, 16 ]
let readPopulations = if comparison then [ 100000 ] else [ 1000; 10000; 100000 ]
let cardinalityCases = if comparison then [ 1000 ] else [ 1; 100; 1000 ]
let writeCases = 1000
let writeActors = 99
let private referencePrefix = "synthetic-growth-"
let private amendedClaimantPrefix = "Synthetic revision "
let private readReferencePrefix = "READ-GROWTH-"
let private claimantPrefix = "Growth claimant "
let private claimantCycle = 100
let private targetClaimant = "Target needle"
let private insurer = "Growth insurer"
let decideEvery = 5
let closeEvery = 2
let readLimit = 50
let warmupRequests = 5
let measuredRequests = 100

let reference index = referencePrefix + string index

let readReference index =
    sprintf "%s%07d" readReferencePrefix index

let readRegistration count index =
    Inputs.registrationWith
        [
            BusinessField.ClaimantName,
            (if index = count - 1 then
                 targetClaimant
             else
                 claimantPrefix + string (index % claimantCycle))
            BusinessField.InsurerName, insurer
        ]
        Inputs.registration

let authorityGrants index =
    [
        yield Role.CaseHandler, GrantTarget.Installation
        for target in 0..8 -> Role.CaseReader, GrantTarget.CaseReference(reference target)
        if index < 7 then
            yield Role.Auditor, GrantTarget.Installation
    ]

let act revision =
    let amended =
        Inputs.registrationWith
            [ BusinessField.ClaimantName, amendedClaimantPrefix + string revision ]
            Inputs.registration

    match (revision - 1L) % 8L with
    | 0L -> AuthoredCaseAct.AmendRegistration amended
    | 1L ->
        AuthoredCaseAct.Decide(
            Inputs.decisionFrom
                [
                    BusinessField.PaymentDecisionDate, "2026-08-04"
                    BusinessField.PayableAmount, "100.00"
                    BusinessField.PayableCurrency, "EUR"
                ]
        )
    | 2L ->
        AuthoredCaseAct.RecordPayment(
            Inputs.paymentFrom [ BusinessField.PaymentDate, "2026-08-05" ]
        )
    | 3L -> AuthoredCaseAct.Bare BareAct.ClearPayment
    | 4L ->
        AuthoredCaseAct.Correct
            {
                Registration = Amendment.Replace amended
                Decision = Change.Keep
                Payment = Change.Keep
            }
    | 5L -> AuthoredCaseAct.Bare BareAct.WithdrawDecision
    | 6L -> AuthoredCaseAct.Bare BareAct.Close
    | _ -> AuthoredCaseAct.Bare BareAct.Reopen

let private element codec value =
    use document = JsonDocument.Parse(Codec.encode codec value)
    document.RootElement.Clone()

let private cycle =
    [| for revision in 1L .. 8L -> element RequestCodecs.caseAct (act revision) |]

let private canonical (value: JsonElement) =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream)

    let rec write (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            writer.WriteStartObject()

            for property in
                value.EnumerateObject()
                |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a.Name, b.Name)) do
                writer.WritePropertyName property.Name
                write property.Value

            writer.WriteEndObject()
        | JsonValueKind.Array ->
            writer.WriteStartArray()

            for item in value.EnumerateArray() do
                write item

            writer.WriteEndArray()
        | _ -> value.WriteTo writer

    write value
    writer.Flush()
    stream.ToArray()

let fingerprint definition =
    let value = JsonSerializer.SerializeToElement definition
    let bytes = canonical value

    {|
        definition = value
        canonicalJson = Encoding.UTF8.GetString bytes
        sha256 = bytes |> SHA256.HashData |> Convert.ToHexStringLower
    |}

let writeFingerprint workload =
    let authority =
        [|
            for index in 0 .. workload.Actors - 1 ->
                authorityGrants index
                |> List.map (fun (role, scope) ->
                    {|
                        role = Role.token role
                        scope =
                            match scope with
                            | GrantTarget.Installation -> "INSTALLATION"
                            | GrantTarget.CaseReference value -> value
                    |})
        |]

    fingerprint
        {|
            writes = workload.Writes
            writers = workload.Writers
            cases = workload.Cases
            actors = workload.Actors
            registration = element RequestCodecs.registration Inputs.registration
            actCycle = cycle
            amendedClaimantPrefix = amendedClaimantPrefix
            referencePrefix = referencePrefix
            lane = "target = lane + (turn % laneCaseCount) * writers"
            authorityGrants = authority
        |}

let cardinalityFingerprint cases =
    fingerprint
        {|
            cases = cases
            caseEntries = 1000
            authorityEntries = 3
            workload =
                writeFingerprint
                    {
                        Writes = 1000 - cases
                        Cases = cases
                        Actors = 0
                        Writers = 1
                    }
        |}

let readFingerprint cases filter =
    fingerprint
        {|
            cases = cases
            referenceExample = readReference 1
            registration = element RequestCodecs.registration (readRegistration cases 0)
            lastRegistration =
                element RequestCodecs.registration (readRegistration cases (cases - 1))
            claimantPrefix = claimantPrefix
            claimantCycle = claimantCycle
            decision = element RequestCodecs.decision Inputs.decision
            decideEvery = decideEvery
            closeEvery = closeEvery
            query =
                element
                    Api.caseList.Input
                    {
                        Page =
                            {
                                AfterCursor = None
                                Limit = readLimit
                            }
                        Filter = filter
                    }
            warmupRequests = warmupRequests
            measuredRequests = measuredRequests
        |}
