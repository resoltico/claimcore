namespace ClaimCore.Application.Storage

open System
open ClaimCore.Application
open ClaimCore.Domain

module internal Credentials =
    type Current =
        {
            RecoveryGeneration: Guid
            RestoreFloor: DateTimeOffset option
            Binding: (ActorId * bool * CredentialBoundary * DateTimeOffset option) option
        }

    let current connection principal token =
        task {
            let kind, issuer, subject = PrincipalKey.parts principal

            let! rows =
                DatabaseSession.rows
                    connection
                    """SELECT r.generation,r.credential_floor,a.actor_id,a.enabled,a.credential_generation,a.credential_cutoff,p.confirmed_at
                       FROM claimcore.recovery_generation r LEFT JOIN claimcore.actors a
                       ON a.principal_key=$1 AND a.principal_kind=$2 AND a.issuer=$3 AND a.subject=$4
                       LEFT JOIN claimcore.credential_publications p
                       ON p.actor_id=a.actor_id AND p.generation=a.credential_generation"""
                    [|
                        box (PrincipalKey.naturalKey principal)
                        box (Vocabulary.token PrincipalKind.vocabulary kind)
                        box issuer
                        box subject
                    |]
                    token

            let row = List.exactlyOne rows

            return
                {
                    RecoveryGeneration = DatabaseSession.value<Guid> 0 row
                    RestoreFloor =
                        DatabaseSession.optional<DateTime> 1 row |> Option.map DateTimeOffset
                    Binding =
                        DatabaseSession.optional<Guid> 2 row
                        |> Option.map (fun id ->
                            DatabaseSession.admitted ActorId.create id,
                            DatabaseSession.value<bool> 3 row,
                            {
                                CredentialGeneration =
                                    DatabaseSession.value<int64> 4 row
                                    |> DatabaseSession.admitted Sequence.create
                                CredentialCutoff = DatabaseSession.instant 5 row
                            },
                            DatabaseSession.optional<DateTime> 6 row |> Option.map DateTimeOffset)
                }
        }

    /// This statement's MVCC snapshot only sees committed generations. Its clock sample
    /// is taken during execution, after the snapshot, and the upsert never replaces
    /// a newer publication. Absent publication grants no provider authority.
    let settle connection token =
        DatabaseSession.execute
            connection
            """WITH observed AS MATERIALIZED(SELECT clock_timestamp() AS instant)
               INSERT INTO claimcore.credential_publications(actor_id,generation,confirmed_at)
               SELECT a.actor_id,a.credential_generation,o.instant
               FROM claimcore.actors a CROSS JOIN observed o
               LEFT JOIN claimcore.credential_publications p ON p.actor_id=a.actor_id
               WHERE a.credential_generation > coalesce(p.generation,0)
               AND o.instant >= greatest(a.credential_cutoff,p.confirmed_at)
               AND abs(extract(epoch FROM (o.instant-$1::timestamptz))) <= $2
               ON CONFLICT(actor_id) DO UPDATE SET generation=EXCLUDED.generation,confirmed_at=EXCLUDED.confirmed_at
               WHERE credential_publications.generation < EXCLUDED.generation"""
            [|
                box (TimeProvider.System.GetUtcNow())
                box CredentialFreshness.maximumLead.TotalSeconds
            |]
            token

    let private floor (current: Current) boundary confirmed =
        current.RestoreFloor
        |> Option.fold max (max boundary.CredentialCutoff confirmed)

    let browser installation observed current proof =
        match current.Binding, proof with
        | Some(actor, true, boundary, Some confirmed), Some proof when
            CredentialFreshness.human observed (floor current boundary confirmed) proof
            ->
            BrowserCredential.Protected(
                installation,
                current.RecoveryGeneration,
                actor,
                boundary.CredentialGeneration
            )
        | _ -> BrowserCredential.StatusOnly

    let usable installation observed principal current proof =
        match current.Binding, proof with
        | Some(actor, true, boundary, Some _),
          VerifiedCredential.Browser(BrowserCredential.Protected(reported,
                                                                 recovery,
                                                                 owner,
                                                                 generation)) ->
            installation = reported
            && current.RecoveryGeneration = recovery
            && actor = owner
            && generation = boundary.CredentialGeneration
        | Some(_, true, boundary, Some confirmed), VerifiedCredential.HumanBearer proof when
            PrincipalKey.isHuman principal
            ->
            CredentialFreshness.human observed (floor current boundary confirmed) proof
        | Some(_, true, boundary, Some confirmed), VerifiedCredential.MachineBearer issued when
            not (PrincipalKey.isHuman principal)
            ->
            CredentialFreshness.after observed (floor current boundary confirmed) issued
        | _ -> false

    let admitsCurrent (tx: ReadTx) principal state =
        match tx.Credential with
        | None -> true // Internal callers supply authority independently of HTTPS.
        | Some(verifiedPrincipal, proof) ->
            verifiedPrincipal = principal
            && usable tx.InstallationId tx.ObservedAt principal state proof

    let admits (tx: ReadTx) principal =
        task {
            match tx.Credential with
            | None -> return true // Internal use cases already supply authority independently of HTTPS.
            | Some(verifiedPrincipal, _) when verifiedPrincipal <> principal -> return false
            | Some _ ->
                let! state = current tx.Connection principal tx.CancellationToken
                return admitsCurrent tx principal state
        }
