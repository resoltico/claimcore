namespace ClaimCore.Application.Verification

open ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application.Verification.VerificationChecks
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Prepare from one owner snapshot; only replacement and readback hold the head lock.
module internal ProjectionRebuild =
    let private attachmentTable =
        ProjectionWrite.table "case_attachments" "case_id,attachment_id" "$1,$2" ""

    let private stagedTables
        (cases: Map<CaseId, FamilyProjectionDelta>)
        (people: Map<ActorId, PersonProjection>)
        attachments
        =
        let families = cases |> Map.values
        let actors = people |> Map.values

        [
            PartProjections.actorTable, actors |> Seq.map PartProjections.actorValues
            PartProjections.caseTable, families |> Seq.map PartProjections.caseValues
            PartProjections.partyTable, families |> Seq.collect PartProjections.partyValues
            PaymentStorage.paymentTable,
            families
            |> Seq.collect (PartProjections.paymentsValues >> Seq.map PaymentStorage.paymentValues)
            PaymentStorage.totalsTable,
            families
            |> Seq.collect (fun row ->
                PaymentStorage.totalsValues
                    row.Snapshot.CaseId
                    (PartProjections.financialTotals row))
            PartProjections.workTable, families |> Seq.map PartProjections.workValues
            PartProjections.reminderTable, families |> Seq.collect PartProjections.reminderValues
            PartProjections.noteTable, families |> Seq.collect PartProjections.noteValues
            PartProjections.linkTable, families |> Seq.collect PartProjections.linkValues
            PartProjections.grantTable, actors |> Seq.collect PartProjections.grantsValues
            attachmentTable,
            attachments
            |> Seq.map (fun (caseId, id) ->
                [| box (CaseId.value caseId); box (AttachmentId.value id) |])
        ]

    let rebuild
        (source: NpgsqlDataSource)
        masterKey
        (expected: InstallationCheckpoint)
        (handle: MaintenanceHandle)
        (token: CancellationToken)
        =
        task {
            let! prepared =
                RegisterMaintenanceStore.prepare
                    source
                    masterKey
                    handle
                    token
                    (fun connection publication cipher ->
                        task {
                            let installation = publication.Installation

                            match
                                CheckpointEvidence.trustedInstallation
                                    installation.InstallationId
                                    expected
                            with
                            | Error finding -> return Error finding
                            | Ok() ->
                                let! scanned =
                                    Verification.authoritative
                                        connection
                                        installation
                                        cipher
                                        (Some expected.Position)
                                        token

                                match scanned with
                                | Error finding -> return Error finding
                                | Ok state ->
                                    let! frontier =
                                        CheckpointEvidence.frontier
                                            connection
                                            (Some expected)
                                            token

                                    match frontier with
                                    | Error finding -> return Error finding
                                    | Ok() ->
                                        let cases = Replay.familyRows state
                                        let people = Replay.peopleRows state

                                        let! before =
                                            Verification.projections
                                                connection
                                                installation.InstallationId
                                                cases
                                                people
                                                state
                                                token

                                        return
                                            result {
                                                let! caseRows = cases
                                                let! peopleRows = people

                                                return
                                                    publication,
                                                    state,
                                                    caseRows,
                                                    peopleRows,
                                                    before
                                            }
                        })

            match prepared with
            | Error finding -> return Error finding
            | Ok(publication, state, _, _, Ok()) ->
                let report =
                    {
                        ProjectionRebuildReport.Position = state.Head
                        PreviousDivergence = None
                    }

                return!
                    RegisterMaintenanceStore.confirm source masterKey publication token (Ok report)

            | Ok(publication, state, caseRows, peopleRows, Error before) ->
                let tables = stagedTables caseRows peopleRows (Replay.attachmentProjection state)

                let stage connection =
                    task {
                        for table, rows in tables do
                            do! ProjectionWrite.stage connection table rows token
                    }

                return!
                    RegisterMaintenanceStore.publishStaged
                        source
                        masterKey
                        publication
                        token
                        stage
                        (fun connection _ ->
                            task {
                                let! _ = PrincipalLock.acquireStagedActors connection token

                                // DELETE and INSERT preserve old readers' MVCC snapshots. The live
                                // tables still check foreign keys and deferred erasure guards.
                                for table, _ in List.rev tables do
                                    let! _ = ProjectionWrite.clear connection table token
                                    ()

                                for table, _ in tables do
                                    let! _ = ProjectionWrite.replace connection table token
                                    ()

                                let! _ =
                                    DatabaseSession.execute
                                        connection
                                        "DELETE FROM claimcore.access_request AS request USING pg_temp.actors AS actor WHERE request.principal_key=actor.principal_key"
                                        [||]
                                        token

                                let! validated =
                                    Verification.projections
                                        connection
                                        publication.Installation.InstallationId
                                        (Ok caseRows)
                                        (Ok peopleRows)
                                        state
                                        token

                                return
                                    validated
                                    |> Result.map (fun () ->
                                        {
                                            ProjectionRebuildReport.Position = state.Head
                                            PreviousDivergence = Some before
                                        })
                            })

        }


    let repairAttachment
        source
        masterKey
        expected
        handle
        caseId
        blobId
        (payload: ReadOnlyMemory<byte>)
        token
        =
        let refused () =
            raise (OperatorFaultException OperatorFault.AttachmentRepairRefused)

        task {
            if payload.Length > AttachmentLimits.FileBytes then
                refused ()

            let bytes = payload.ToArray()

            try
                let key = caseId, blobId

                let! prepared =
                    RegisterMaintenanceStore.prepare
                        source
                        masterKey
                        handle
                        token
                        (fun connection publication cipher ->
                            task {
                                let installation = publication.Installation

                                match
                                    CheckpointEvidence.trustedInstallation
                                        installation.InstallationId
                                        expected
                                with
                                | Error finding -> return Error finding
                                | Ok() ->
                                    let! checkedHistory =
                                        Verification.history
                                            connection
                                            installation
                                            cipher
                                            (Some expected.Position)
                                            token

                                    match checkedHistory with
                                    | Error finding -> return Error finding
                                    | Ok state ->
                                        let! frontier =
                                            CheckpointEvidence.frontier
                                                connection
                                                (Some expected)
                                                token

                                        let! references =
                                            AttachmentStorage.checkReferences
                                                connection
                                                (Replay.attachmentReferences state)
                                                token

                                        return
                                            result {
                                                do! frontier
                                                do! references

                                                match
                                                    Map.tryFind key (Replay.attachmentBlobs state)
                                                with
                                                | Some(at, Some descriptor) ->
                                                    let descriptors =
                                                        Replay.attachmentDescriptors state

                                                    if
                                                        AttachmentStorage.matchesDescriptors
                                                            descriptors[key]
                                                            bytes
                                                    then
                                                        return publication, at, descriptor
                                                    else
                                                        return refused ()
                                                | _ -> return refused ()
                                            }
                            })

                match prepared with
                | Error finding -> return Error finding
                | Ok(publication, at, descriptor) ->
                    let! reserved = EncryptionBudget.reserveActive source 1L token

                    return!
                        RegisterMaintenanceStore.publish
                            source
                            masterKey
                            publication
                            token
                            (fun connection cipher ->
                                task {
                                    if cipher.Generation <> reserved then
                                        refused ()

                                    use encrypting = cipher.ForEncryption 1L

                                    let! repaired =
                                        AttachmentStorage.repair
                                            connection
                                            encrypting
                                            key
                                            at
                                            descriptor
                                            bytes
                                            token

                                    if not repaired then
                                        refused ()

                                    return
                                        Ok
                                            {
                                                AttachmentRepairReport.CaseId = caseId
                                                BlobId = blobId
                                                AcceptedAt = at
                                            }
                                })
            finally
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(Span bytes)
        }
