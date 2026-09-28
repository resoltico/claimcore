namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ActorGrantRegistryQueries

module internal ActorManagementGrant =
    let private scopeFor connection transaction active =
        function
        | GrantTarget.Installation -> Task.FromResult(Some GrantScope.Installation)
        | GrantTarget.CaseReference reference ->
            task {
                let! id =
                    if active then
                        ActorGrantGateQueries.caseIdByReference
                            connection
                            transaction
                            reference
                            CancellationToken.None
                    else
                        ActorGrantGateQueries.caseIdByReferenceAny
                            connection
                            transaction
                            reference
                            CancellationToken.None

                return id |> Option.map GrantScope.Case
            }

    let private observed
        eventId
        actorId
        approverId
        role
        grantScope
        active
        (action: ActorAuthorityAction)
        =
        let actionName = if active then "GRANT_ROLE" else "REVOKE_ROLE"

        action.EventId = eventId
        && action.ActionName = actionName
        && action.TargetActorId = actorId
        && action.ApproverActorId = Some approverId
        && action.Grant = Some { Role = role; Scope = grantScope }
        && action.Enabled = Some active

    let private validChange connection transaction actorId grant active =
        task {
            let! target = targetState connection transaction actorId
            let! current = grantState connection transaction actorId grant
            let! validCase = caseExists connection transaction active grant.Scope

            let humanAllowed =
                match target with
                | Some(_, "HUMAN") -> true
                | Some _ -> grant.Role <> Role.Owner && grant.Role <> Role.DataSteward
                | None -> false

            let! ownerSafe =
                if active || grant.Role <> Role.Owner || grant.Scope <> GrantScope.Installation then
                    Task.FromResult true
                else
                    remainingOwner connection transaction Guid.Empty actorId

            return
                validCase
                && humanAllowed
                && ownerSafe
                && current <> Some active
                && (active || current.IsSome)
        }

    let private commitGrant
        connection
        transaction
        (witness: WitnessProtocol)
        eventId
        revision
        approverId
        actorId
        grant
        active
        =
        task {
            let action: ActorAuthorityAction =
                {
                    EventId = eventId
                    Revision = revision + 1L
                    ActionName = if active then "GRANT_ROLE" else "REVOKE_ROLE"
                    TargetActorId = actorId
                    ApproverActorId = Some approverId
                    Principal = None
                    Grant = Some grant
                    Enabled = Some active
                }

            let! outcome =
                ActorGrantWrite.run connection transaction witness action (fun () ->
                    ActorGrantWrite.setGrant
                        connection
                        transaction
                        actorId
                        grant
                        active
                        action.Revision)

            return
                match outcome with
                | AuthorityWriteOutcome.Applied(_, value) ->
                    ActorManagementOutcome.Applied(eventId, value, actorId)
                | AuthorityWriteOutcome.Refused -> ActorManagementOutcome.ResourceUnavailable
                | AuthorityWriteOutcome.Unconfirmed _ -> ActorManagementOutcome.Unconfirmed eventId
        }

    let setGrant
        (witness: WitnessProtocol)
        eventId
        target
        role
        scope
        active
        connection
        transaction
        revision
        approverId
        =
        task {
            let! target = targetId connection transaction target
            let! scoped = scopeFor connection transaction active scope

            match target, scoped with
            | None, _
            | _, None -> return ActorManagementOutcome.ResourceUnavailable
            | Some actorId, Some grantScope ->
                let grant = { Role = role; Scope = grantScope }
                let! seen = existingEvent connection transaction witness eventId

                match seen with
                | Some action when observed eventId actorId approverId role grantScope active action ->
                    return ActorManagementOutcome.Applied(eventId, action.Revision, actorId)
                | Some _ -> return ActorManagementOutcome.ResourceUnavailable
                | None ->
                    let! valid = validChange connection transaction actorId grant active

                    if not valid then
                        return ActorManagementOutcome.ResourceUnavailable
                    else
                        return!
                            commitGrant
                                connection
                                transaction
                                witness
                                eventId
                                revision
                                approverId
                                actorId
                                grant
                                active
        }
