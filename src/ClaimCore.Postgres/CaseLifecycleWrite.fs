namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain

module internal CaseLifecycleWrite =
    let private one connection transaction sql bind =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            bind command
            let! affected = command.ExecuteNonQueryAsync()

            if affected <> 1 then
                raise (
                    InvalidDataException("Lifecycle persistence affected an unexpected row count.")
                )
        }

    let private updateProjection
        connection
        transaction
        (projection: LifecycleProjection)
        (decision: LifecycleDecisionResult)
        eventHash
        =
        let revision =
            decision.Snapshot
            |> Option.map _.Version
            |> Option.defaultValue (Claim.view projection.Claim).Version

        one
            connection
            transaction
            ("UPDATE claimcore.cases SET revision=@nextRevision,disposition=@disposition,"
             + "privacy_phase=@privacy,lifecycle_sequence=@nextSequence,"
             + "lifecycle_event_hash=@nextHash WHERE case_id=@caseId "
             + "AND revision=@previousRevision AND lifecycle_sequence=@previousSequence "
             + "AND lifecycle_event_hash=@previousHash")
            (fun command ->
                Sql.integer command "nextRevision" revision

                Sql.text
                    command
                    "disposition"
                    (CaseLifecycleCandidate.dispositionName (
                        CaseLifecycle.disposition decision.State
                    ))

                Sql.text
                    command
                    "privacy"
                    (CaseLifecycleCandidate.privacyName (CaseLifecycle.privacy decision.State))

                Sql.integer command "nextSequence" (projection.Sequence + 1L)
                Sql.add command "nextHash" NpgsqlDbType.Bytea (box eventHash)
                Sql.uuid command "caseId" projection.CaseId
                Sql.integer command "previousRevision" (Claim.view projection.Claim).Version
                Sql.integer command "previousSequence" projection.Sequence
                Sql.add command "previousHash" NpgsqlDbType.Bytea (box projection.EventHash))

    let private updateHold connection transaction caseId actorId instant mutation =
        match mutation with
        | LifecycleMutation.RecordHold(holdId, ground, reviewOn) ->
            one
                connection
                transaction
                ("INSERT INTO claimcore.case_holds "
                 + "(hold_id,case_id,ground,review_on,recorded_by,recorded_at) "
                 + "VALUES (@hold,@caseId,@ground,@review,@actor,@instant)")
                (fun command ->
                    Sql.uuid command "hold" holdId
                    Sql.uuid command "caseId" caseId
                    Sql.text command "ground" ground
                    Sql.add command "review" NpgsqlDbType.Date (box reviewOn)
                    Sql.uuid command "actor" actorId
                    Sql.add command "instant" NpgsqlDbType.TimestampTz (box instant))
        | LifecycleMutation.ReleaseHold(holdId, reason) ->
            one
                connection
                transaction
                ("UPDATE claimcore.case_holds SET released_by=@actor,released_at=@instant,"
                 + "release_reason=@reason WHERE hold_id=@hold AND case_id=@caseId "
                 + "AND released_at IS NULL")
                (fun command ->
                    Sql.uuid command "actor" actorId
                    Sql.add command "instant" NpgsqlDbType.TimestampTz (box instant)
                    Sql.text command "reason" reason
                    Sql.uuid command "hold" holdId
                    Sql.uuid command "caseId" caseId)
        | _ -> task { return () }

    let private eventSql =
        "INSERT INTO claimcore.case_lifecycle_events "
        + "(event_id,case_id,case_reference,lifecycle_sequence,business_revision,"
        + "action_name,actor_id,grant_revision,draft_sha256,canonical_action,"
        + "candidate_sha256,previous_hash,event_hash,witness_sequence,witness_epoch,"
        + "witness_entry_hash) VALUES "
        + "(@event,@caseId,@reference,@sequence,@revision,@action,@actor,@grant,"
        + "@draft,@canonical,@candidate,@previous,@eventHash,@witnessSequence,"
        + "@witnessEpoch,@witnessHash)"

    let private insertEvent
        connection
        transaction
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        revision
        draftHash
        canonical
        candidateHash
        eventHash
        (intent: WitnessIntent)
        (actor: ActorCallContext)
        =
        one connection transaction eventSql (fun command ->
            Sql.uuid command "event" change.EventId
            Sql.uuid command "caseId" projection.CaseId
            Sql.text command "reference" change.CaseReference
            Sql.integer command "sequence" (projection.Sequence + 1L)
            Sql.integer command "revision" revision
            Sql.text command "action" (CaseLifecycleCandidate.actionName change.Action)
            Sql.uuid command "actor" actor.Binding.ActorId
            Sql.integer command "grant" actor.Binding.GrantRevision
            Sql.add command "draft" NpgsqlDbType.Bytea (box draftHash)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box candidateHash)
            Sql.add command "previous" NpgsqlDbType.Bytea (box projection.EventHash)
            Sql.add command "eventHash" NpgsqlDbType.Bytea (box eventHash)
            Sql.integer command "witnessSequence" intent.Ticket.Sequence
            Sql.integer command "witnessEpoch" intent.Ticket.Epoch
            Sql.add command "witnessHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash))

    let private persistErasure
        connection
        transaction
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (decision: LifecycleDecisionResult)
        revision
        eventHash
        candidateHash
        (intent: WitnessIntent)
        (commitments: ISuppressionCommitments)
        denials
        =
        task {
            match change.Action, denials with
            | LifecycleMutation.RequestErasure _, Some(count, digest) ->
                do!
                    CaseErasureFenceWrite.persist
                        connection
                        transaction
                        projection
                        change
                        decision
                        revision
                        eventHash
                        candidateHash
                        intent
                        commitments
                        count
                        digest
            | LifecycleMutation.RequestErasure _, None ->
                invalidOp "Erasure operation denials were not captured."
            | _, None -> ()
            | _, Some _ -> invalidOp "Non-erasure lifecycle event carried denial evidence."
        }

    let private persistProjectionAndEvent
        connection
        transaction
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (decision: LifecycleDecisionResult)
        revision
        draftHash
        canonical
        candidateHash
        eventHash
        (intent: WitnessIntent)
        (actor: ActorCallContext)
        instant
        =
        task {
            do! updateProjection connection transaction projection decision eventHash

            do!
                updateHold
                    connection
                    transaction
                    projection.CaseId
                    actor.Binding.ActorId
                    instant
                    change.Action

            do!
                insertEvent
                    connection
                    transaction
                    projection
                    change
                    revision
                    draftHash
                    canonical
                    candidateHash
                    eventHash
                    intent
                    actor
        }

    let private revision (projection: LifecycleProjection) (decision: LifecycleDecisionResult) =
        decision.Snapshot
        |> Option.map _.Version
        |> Option.defaultValue (Claim.view projection.Claim).Version

    let persistEvent
        connection
        transaction
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (decision: LifecycleDecisionResult)
        draftHash
        canonical
        candidateHash
        eventHash
        (intent: WitnessIntent)
        (actor: ActorCallContext)
        instant
        (commitments: ISuppressionCommitments)
        denials
        =
        task {
            let revision = revision projection decision

            do!
                persistProjectionAndEvent
                    connection
                    transaction
                    projection
                    change
                    decision
                    revision
                    draftHash
                    canonical
                    candidateHash
                    eventHash
                    intent
                    actor
                    instant

            do!
                persistErasure
                    connection
                    transaction
                    projection
                    change
                    decision
                    revision
                    eventHash
                    candidateHash
                    intent
                    commitments
                    denials

            return revision, projection.Sequence + 1L
        }
