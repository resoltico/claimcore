module ClaimCore.Tests.ExportDeliveryTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Tests.Fixtures

let private faultExit fault =
    let bytes = WebWireCodec.recoveryExport (RecoveryQueryOutcome.RecoveryFailed fault)
    use document = JsonDocument.Parse(ReadOnlyMemory bytes)
    (CliRemoteWireCodec.result "recovery.export" document.RootElement).ExitCode

let private effects =
    testCase
        "[CC-CLI-002] witnessed export is noncancellable and lost completion stays uncertain"
        (fun () ->
            let model = ContractProjection.current ()

            let endpoint =
                model.CliEndpoints
                |> List.find (fun endpoint -> endpoint.Identifier = "recovery.export")

            Expect.isFalse endpoint.Cancellable "Export records witnessed custody"

            Expect.isTrue
                (CliMutationCatalog.isMutation endpoint.Identifier)
                "No read-only delivery classification"

            let bytes = WebWireCodec.hostFailure WebHostFailure.CompletedResponseFailed
            use document = JsonDocument.Parse(ReadOnlyMemory bytes)

            Expect.equal
                (CliRemoteWireCodec.hostFailure endpoint.Identifier document.RootElement).ExitCode
                4
                "No false definite failure")

let private explicitKnowledge =
    testCase "[CC-CLI-002] FAILED export preserves core-owned uncertainty guidance" (fun () ->
        Expect.equal
            (faultExit CoreFault.RecoveryMutationUnknown)
            4
            "Technical issuance may have committed"

        Expect.equal
            (faultExit CoreFault.CommitOutcomeUnknown)
            4
            "Accepted outcome remains unknown"

        Expect.equal
            (faultExit CoreFault.RecoveryStoreUnavailable)
            3
            "Definite pre-completion fault keeps its guidance")

let private destinationKnowledge =
    testCase
        "[CC-CLI-002] private destination failure retains observed service completion"
        (fun () ->
            let reply =
                CliRemoteWireCodec.localFailure
                    "recovery.export"
                    CliRemoteProblem.PrivateDestination

            use document = JsonDocument.Parse(ReadOnlyMemory reply.Bytes)

            Expect.equal
                (document.RootElement.GetProperty("executionPhase").GetString())
                "RESULT_OBSERVED"
                "Export response was observed"

            let schema = CliSchemas.responseDocument (ContractProjection.current ())

            Expect.isTrue
                (SchemaValueValidation.verify schema document.RootElement)
                "Generated CLI contract admits the precise phase")

let private lateCancellation =
    testCase "[CC-REC-001] signed export completion cannot become a late cancellation" (fun () ->
        let claims = new CoreStore.Store()
        let recovery = new CoreRecoveryStore.Store()
        recovery.AttachClaimStore(claims :> IClaimStore)

        let core =
            ActorCoreFixture.create
                (claims :> IClaimStore)
                (recovery :> IRecoveryStore)
                (businessTime today)

        let input = request 0L (ClaimCore.Domain.Command.Open registration)

        match core.Prepare(input, CancellationToken.None).Result with
        | PrepareOutcome.Prepared _ -> ()
        | _ -> failtest "Synthetic preparation must be retained"

        use cancellation = new CancellationTokenSource()

        let authority =
            { new IRecoveryArtifactAuthority with
                member _.Sign(_, _) =
                    cancellation.Cancel()
                    Task.FromResult(Ok [| 1uy |])

                member _.Verify(_, _) =
                    Task.FromResult(Error RecoveryRejection.EnvelopeInvalidOrUnsupported)
            }

        let digest = Operation.prepare input |> accepted |> Operation.fingerprint

        let result =
            RecoveryExports.export
                (recovery :> IRecoveryStore)
                authority
                input.OperationId
                digest
                cancellation.Token
            |> fun task -> task.GetAwaiter().GetResult()

        match result with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) -> ()
        | _ -> failtest "Observed successful issuance must retain its definite outcome")

let tests =
    testList
        "export effect and delivery ownership"
        [ effects; explicitKnowledge; destinationKnowledge; lateCancellation ]
