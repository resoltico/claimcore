module ClaimCore.IntegrationTests.MutationDisclosureTests

open System.Threading
open System
open System.Security.Cryptography
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.MutationDisclosureFixture

let private prepareRevocation () =
    setup (fun owner (source, app) witness (runtime: Runtime) principal _ _ _ _ ->
        let request = newRequest ()

        let beforeFence () =
            grant source witness principal Role.CaseEditor false

        use admission = fencedAdmission app witness beforeFence
        let actor = runtime.ForActorWithAdmission(principal, admission)
        requireWithheld (fun () -> actor.Prepare(request, cancellation) |> await |> ignore)

        Expect.equal
            (retainedCount owner request.OperationId)
            1L
            "Retention was committed before lost disclosure"

        Expect.equal
            (acceptedCount owner request.OperationId)
            0L
            "Preparation did not execute the command")

let private executeFence privacy =
    setup (fun owner (source, app) witness (runtime: Runtime) principal _ _ _ _ ->
        let request = newRequest ()

        let beforeFence () =
            Expect.equal
                (acceptedCount owner request.OperationId)
                1L
                "The interleaving occurs after primary COMMIT"

            Expect.isSome
                ((witness.EvidenceStore
                    .TryReadEvidence(request.OperationId, SettledAccepted, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()))
                "The acceptance is independently settled"

            if privacy then
                erase (runtime.ForActor principal) request.CaseReference
            else
                grant source witness principal Role.CaseEditor false

        use admission = fencedAdmission app witness beforeFence
        let actor = runtime.ForActorWithAdmission(principal, admission)
        requireWithheld (fun () -> actor.Execute(request, cancellation) |> await |> ignore)

        Expect.equal
            (acceptedCount owner request.OperationId)
            1L
            "Withholding never rolls back accepted authority")

let private recoveryFence action role =
    setup (fun owner (source, app) witness (runtime: Runtime) principal _ _ _ _ ->
        grant source witness principal role true
        let normal = runtime.ForActor principal
        let request = newRequest ()
        let digest = prepared normal request

        use admission =
            fencedAdmission app witness (fun () -> grant source witness principal role false)

        let actor = runtime.ForActorWithAdmission(principal, admission)
        requireWithheld (fun () -> action actor request.OperationId digest)

        Expect.equal
            (retainedCount owner request.OperationId)
            1L
            "Committed recovery material survives disclosure refusal")

let private exportRevocation () =
    recoveryFence
        (fun actor operation digest ->
            actor.Recovery.ExportEnvelope(operation, digest, cancellation)
            |> await
            |> ignore)
        Role.RecoveryExporter

let private resolveRevocation () =
    recoveryFence
        (fun actor operation digest ->
            actor.Recovery.Resolve(operation, digest, cancellation) |> await |> ignore)
        Role.RecoveryOperator

let private dismissRevocation () =
    recoveryFence
        (fun actor operation digest ->
            actor.Recovery.Dismiss(operation, digest, true, cancellation) |> await |> ignore)
        Role.RecoveryOperator

let private retainRevocation () =
    setup (fun owner (source, app) witness (runtime: Runtime) principal _ _ _ _ ->
        grant source witness principal Role.RecoveryOperator true
        grant source witness principal Role.RecoveryExporter true
        let normal = runtime.ForActor principal
        let request = newRequest ()
        let digest = prepared normal request
        let artifact = exported normal request.OperationId digest

        try
            let sourceDigest = artifact.Bytes |> SHA256.HashData |> Convert.ToHexStringLower

            use admission =
                fencedAdmission app witness (fun () ->
                    grant source witness principal Role.RecoveryOperator false)

            let actor = runtime.ForActorWithAdmission(principal, admission)

            requireWithheld (fun () ->
                actor.Recovery.RetainEnvelopeImport(artifact.Bytes, sourceDigest, cancellation)
                |> await
                |> ignore)

            Expect.equal
                (retainedCount owner request.OperationId)
                1L
                "Exact import retained its original operation"
        finally
            CryptographicOperations.ZeroMemory artifact.Bytes)

let tests =
    testList
        "mutation result disclosure"
        [
            testCase
                "[CC-AUTH-001] retained preparation is withheld after editor revocation"
                prepareRevocation
            testCase
                "[CC-AUTH-001] accepted command result is withheld after editor revocation"
                (fun () -> executeFence false)
            testCase
                "[CC-AUTH-001] accepted command result is withheld after erasure fence"
                (fun () -> executeFence true)
            testCase
                "[CC-AUTH-001] witnessed export is withheld after exporter revocation"
                exportRevocation
            testCase
                "[CC-AUTH-001] accepted recovery resolution is withheld after operator revocation"
                resolveRevocation
            testCase
                "[CC-AUTH-001] revoked preparation details are withheld after operator revocation"
                dismissRevocation
            testCase
                "[CC-AUTH-001] exact import result is withheld after importer revocation"
                retainRevocation
        ]
