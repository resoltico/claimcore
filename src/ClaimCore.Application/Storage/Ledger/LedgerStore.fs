namespace ClaimCore.Application.Storage

open ClaimCore.Application.Verification

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application.Contracts

type internal LedgerStore
    (
        source: NpgsqlDataSource,
        installation: Installation,
        cipher: InstallationCipher,
        settings,
        ?caseReferenceEntropy: unit -> byte array,
        ?witness: RecoveryWitness * WitnessPrimary
    ) =
    static let drawEntropy () =
        System.Security.Cryptography.RandomNumberGenerator.GetBytes CaseReference.EntropyBytes

    let caseReferenceEntropy = defaultArg caseReferenceEntropy drawEntropy

    static member InTransaction(connection, installation, cipher, head, observedAt, token) =
        WriteTx(connection, installation, cipher, head, observedAt, drawEntropy, token)

    interface IStore with
        member _.Context query =
            Transactions.read source query.CancellationToken (fun connection observedAt ->
                task {
                    do! Baseline.requireExpected installation connection query.CancellationToken

                    let tx =
                        ReadTx(
                            connection,
                            installation,
                            cipher,
                            observedAt,
                            query.CancellationToken
                        )

                    let! facts =
                        Projections.globalReadFacts
                            tx.Connection
                            query.Principal
                            tx.CancellationToken

                    tx.Credential <- query.Credential
                    let! usable = Credentials.admits tx query.Principal

                    let! association =
                        PersonAcquisition.association tx query.Principal facts.People.Actor

                    let! pending =
                        match association with
                        | PrincipalAssociation.NeverBound ->
                            AccessRequests.pending tx query.Principal
                        | _ -> System.Threading.Tasks.Task.FromResult false

                    let! authorityVersion =
                        match association with
                        | PrincipalAssociation.Current actor when actor.Enabled && usable ->
                            PersonAcquisition.authorityVersion tx actor.ActorId
                        | _ -> System.Threading.Tasks.Task.FromResult 0L

                    let! terminationCutoff =
                        SessionRows.terminationCutoff connection query.CancellationToken

                    return
                        {
                            TerminationCutoff = terminationCutoff
                            ContextFacts.Facts = facts
                            Association = association
                            RequestPending = pending
                            CredentialUsable = usable
                            AuthorityVersion = authorityVersion
                            ObservedAt = observedAt
                        }
                })

        member _.Read
            ({
                 ReadOperation.Work = work
                 CancellationToken = token
             })
            =
            Transactions.read source token (fun connection observedAt ->
                task {
                    do! Baseline.requireExpected installation connection token
                    return! work (ReadTx(connection, installation, cipher, observedAt, token))
                })

        member _.RecordDisclosure(event, token) =
            let append connection =
                task {
                    do! Baseline.requireExpected installation connection token
                    let! _ = WitnessPrimary.configureDisclosure connection token
                    let! _ = DisclosureStorage.append connection event token
                    return (), true
                }

            match witness with
            | None -> Transactions.write source settings token append
            | Some(evidence, admitted) ->
                task {
                    let! committed =
                        Transactions.writeAndObserveCommit
                            source
                            settings
                            token
                            append
                            WitnessPrimary.read

                    match committed with
                    | Error failure -> return Error failure
                    | Ok((), primary) when
                        primary.Generation = admitted.Generation
                        && primary.PrimaryId = admitted.PrimaryId
                        && primary.SystemId = admitted.SystemId
                        && primary.Timeline = admitted.Timeline
                        ->
                        // The provider has disposed its main connection before any independent wait.
                        let! acknowledged = evidence.Acknowledge(primary, token)

                        return
                            if acknowledged then
                                Ok()
                            else
                                Error StoreFailure.Unavailable
                    | Ok _ -> return Error StoreFailure.Unavailable
                }

        member _.Write(operation) =
            let token = operation.CancellationToken

            let run () =
                task {
                    let mutable personProjected = false

                    let! result =
                        Transactions.write source settings token (fun connection ->
                            task {
                                do! Baseline.requireExpected installation connection token
                                let! head, observedAt = Transactions.head connection token
                                FailureDiagnostics.phase FailurePhase.Read

                                if operation.RequiresEncryption then
                                    let! current =
                                        Baseline.currentEncryptionGeneration
                                            connection
                                            cipher.Generation
                                            token

                                    if not current then
                                        raise (
                                            RecordCorruptException
                                                "The write reservation belongs to a retired encryption generation."
                                        )

                                let invoke scope =
                                    task {
                                        let tx =
                                            WriteTx(
                                                connection,
                                                installation,
                                                scope,
                                                head,
                                                observedAt,
                                                caseReferenceEntropy,
                                                token
                                            )

                                        let! outcome = operation.Work tx
                                        personProjected <- tx.PersonProjected
                                        return outcome
                                    }

                                let! verdict =
                                    task {
                                        if operation.RequiresEncryption then
                                            use allocated =
                                                cipher.ForEncryption
                                                    InstallationCipher.operationAllowance

                                            return! invoke allocated
                                        else
                                            return! invoke cipher
                                    }

                                let commit =
                                    match verdict with
                                    | Verdict.Proceed _ -> true
                                    | Verdict.Replay _
                                    | Verdict.Refuse _
                                    | Verdict.Fail _ -> false

                                return verdict, commit
                            })

                    match result with
                    | Ok(Verdict.Proceed _) when personProjected ->
                        do!
                            Transactions.settleCredentials
                                source
                                settings
                                (Baseline.requireExpected installation)
                                token
                    | _ -> ()

                    return result
                }

            if not operation.RequiresEncryption then
                run ()
            else
                task {
                    match!
                        EncryptionBudget.reserve
                            source
                            cipher.Generation
                            InstallationCipher.operationAllowance
                            token
                    with
                    | Ok true -> return! run ()
                    | Ok false -> return Error StoreFailure.Unavailable
                    | Error failure -> return Error failure
                }
