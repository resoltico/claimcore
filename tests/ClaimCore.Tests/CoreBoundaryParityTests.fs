module ClaimCore.Tests.CoreBoundaryParityTests

open System
open System.Text
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private operationId = Guid.Parse("30000000-0000-4000-8000-000000000701")

let private values =
    [
        "incidentDate", registration.IncidentDate
        "incidentNotificationDate", registration.IncidentNotificationDate
        "incidentCountry", registration.IncidentCountry
        "claimantName", registration.ClaimantName
        "insurerName", registration.InsurerName
        "claimedAmount", registration.ClaimedAmount
        "claimedCurrency", registration.ClaimedCurrency
    ]

let private draft =
    {
        OperationId = operationId
        CaseReference = "BOUNDARY-PARITY-001"
        ExpectedVersion = 0L
        Kind = CommandKind.Open
        Values = values
    }

let private core () =
    let clock =
        { new IBusinessDate with
            member _.Today() = today
        }

    CoreApi.create
        (new CoreStore.Store() :> IClaimStore)
        (new CoreRecoveryStore.Store() :> IRecoveryStore)
        clock

let private v3Frame claimant =
    $"""{{"protocolVersion":3,"endpoint":"command.execute","input":{{"operationId":"{operationId:D}","caseReference":"BOUNDARY-PARITY-001","expectedRevision":"0","command":{{"kind":"OPEN","values":{{"incidentDate":"2026-08-01","incidentNotificationDate":"2026-08-03","incidentCountry":"Lithuania","claimantName":"{claimant}","insurerName":"Example Alleged Insurer","claimedAmount":"1000.00","claimedCurrency":"EUR"}}}}}}}}"""

let private decodeFrame (source: string) =
    match StrictJson.parseDocument 131072 (Encoding.UTF8.GetBytes(source)) with
    | Error _ -> Error()
    | Ok document ->
        use frame = document

        match InvocationFraming.decode frame.RootElement with
        | Ok(Endpoint.CommandExecute, EndpointInput.Draft parsed, None) -> Ok parsed
        | _ -> Error()

let private receipt outcome =
    match outcome with
    | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted accepted, _) -> accepted
    | _ -> failtest "Expected accepted typed core execution."

let private decodedParity =
    testCase "typed and CLI-v3 decoded drafts receive the same core decision" (fun () ->
        let decoded =
            v3Frame registration.ClaimantName
            |> decodeFrame
            |> Result.defaultWith (fun _ -> failtest "The strict v3 frame must decode.")

        let runtime = core ()

        let first =
            runtime.Execute(draft, CancellationToken.None).GetAwaiter().GetResult()
            |> receipt

        match runtime.Execute(decoded, CancellationToken.None).GetAwaiter().GetResult() with
        | SubmissionOutcome.ObservedAccepted replay ->
            Expect.isTrue replay.Replayed "Decoded equivalent request exactly replays"
            Expect.equal replay.Snapshot.Fields first.Snapshot.Fields "Same Domain decision"
            Expect.equal replay.Snapshot.Version first.Snapshot.Version "One accepted revision"
        | _ -> failtest "The JSON-decoded equivalent must replay the original acceptance.")

let private advisoryNonAuthority =
    testCase "forged advisory review cannot affect accepted state" (fun () ->
        let runtime = core ()

        let review =
            match runtime.Prepare(draft, CancellationToken.None).GetAwaiter().GetResult() with
            | PrepareOutcome.Prepared(_, value) -> value
            | _ -> failtest "Expected a Domain-derived advisory review."

        let forged =
            { review with
                Proposed =
                    { review.Proposed with
                        Fields =
                            { review.Proposed.Fields with
                                ClaimantName = "Forged claimant"
                            }
                    }
                Changes = []
                IsAdvisory = false
            }

        Expect.notEqual forged.Proposed.Fields.ClaimantName registration.ClaimantName "Forgery"

        let accepted =
            runtime.Execute(draft, CancellationToken.None).GetAwaiter().GetResult()
            |> receipt

        Expect.equal
            accepted.Snapshot.Fields.ClaimantName
            registration.ClaimantName
            "Execute revalidates the original authored draft, not the caller's review")

let private malformedUnicode =
    testCase "unpaired escaped Unicode cannot alias a valid claimant request" (fun () ->
        let valid = v3Frame registration.ClaimantName |> decodeFrame
        Expect.isOk valid "A valid comparison request decodes"

        let malformed = v3Frame "\\uD800"

        Expect.isError
            (decodeFrame malformed)
            "Malformed escape cannot become a replacement scalar"

        let malformedProperty =
            v3Frame registration.ClaimantName
            |> _.Replace("\"claimantName\"", "\"\\uD800\"")

        Expect.isError (decodeFrame malformedProperty) "Malformed property name cannot be decoded"

        let record =
            {
                OperationId = operationId
                CaseReference = draft.CaseReference
                ExpectedVersion = 0L
                Command = Command.Open registration
            }

        let canonical = RequestRecord.encode record |> Encoding.UTF8.GetString

        let invalidRecord =
            canonical.Replace(
                "\"claimantName\":\"Example Claimant Ltd\"",
                "\"claimantName\":\"\\uD800\""
            )

        Expect.notEqual invalidRecord canonical "The malformed record fixture changes one scalar"

        Expect.isError
            (RequestRecord.decode 65536 (Encoding.UTF8.GetBytes(invalidRecord)))
            "Durable canonical decoding also refuses the invalid scalar")

let private storageFailurePrivacy =
    testCase "typed recovery storage failure and CLI wire response omit authored values" (fun () ->
        let canary = "SYNTHETIC_CLAIMANT_CANARY"

        let authored =
            { draft with
                OperationId = Guid.Parse("30000000-0000-4000-8000-000000000702")
                Values =
                    values
                    |> List.map (fun (name, value) ->
                        name, if name = "claimantName" then canary else value)
            }

        let clock =
            { new IBusinessDate with
                member _.Today() = today
            }

        let runtime =
            CoreApi.create
                (new CoreStore.Store() :> IClaimStore)
                (new CoreRecoveryStore.Store(RecoveryStoreFailure.StoreUnavailable)
                :> IRecoveryStore)
                clock

        let outcome =
            runtime.Prepare(authored, CancellationToken.None).GetAwaiter().GetResult()

        match outcome with
        | PrepareOutcome.PrepareFailed(_, fault) ->
            Expect.equal fault.Code FaultCode.StoreUnavailable "Typed failure classification"
            Expect.isFalse (fault.Message.Contains(canary)) "Core fault omits claimant text"
        | _ -> failtest "Expected a definite technical preparation failure."

        let response = ClaimCore.Contracts.CliWireCodec.prepare "command.prepare" outcome

        let rendered = response.Bytes |> Encoding.UTF8.GetString

        Expect.isFalse (rendered.Contains(canary)) "CLI response omits claimant text")

let tests =
    testList
        "typed core boundary parity"
        [ decodedParity; advisoryNonAuthority; malformedUnicode; storageFailurePrivacy ]
