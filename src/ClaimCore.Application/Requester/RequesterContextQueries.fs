namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Authorizes the read and resolves its result within one store snapshot.
module internal RequesterContextQueries =
    open QueryResults

    let contextResult
        (services: ServiceContext)
        observedAt
        (maintenance: RegisterMaintenance)
        (actor: AccessView)
        (roles: ActorRoleSummary list)
        association
        pending
        credentialUsable
        authorityVersion
        terminationCutoff
        =
        let principal = services.Principal

        let active (requester: Actor) : ActorContext =
            let _, issuer, _ = PrincipalKey.parts principal

            {
                Issuer = issuer
                Roles = roles |> ActorRoleSummary.ordered
                AuthorityVersion = authorityVersion
                Self = Actor.currentReference requester
                Maintenance = maintenance
                InstallationId = services.InstallationId
                ActorId = requester.ActorId
                Runtime = ServiceContext.runtime services observedAt
                HasScopedCases = actor.HasScopedCases
                View = Advice.views principal actor
                Offers =
                    if maintenance.Active then
                        []
                    else
                        Advice.installationOffers principal actor
                Capability =
                    Capability.all
                    |> List.filter (fun capability ->
                        Access.can principal actor capability Scope.Installation)
            }

        let state =
            match association with
            | PrincipalAssociation.Former -> Ok RequesterState.FormerIdentity
            | PrincipalAssociation.NeverBound ->
                let _, issuer, subject = PrincipalKey.parts principal

                let requester =
                    {
                        UnregisteredRequester.Issuer = issuer
                        Subject = subject
                        Claims = services.Claims
                    }

                if pending then
                    Ok(RequesterState.AccessPending requester)
                else
                    Ok(RequesterState.NotRegistered requester)
            | PrincipalAssociation.Current requester when
                AccountState.classify requester.Enabled actor.HasAssignedRoles =
                    AccountState.Disabled
                ->
                Ok(RequesterState.Disabled(Actor.currentReference requester))
            | PrincipalAssociation.Current _ when not credentialUsable ->
                Ok RequesterState.SignInRequired
            | PrincipalAssociation.Current requester when
                AccountState.classify requester.Enabled actor.HasAssignedRoles =
                    AccountState.NoRoles
                ->
                let basis = ServiceContext.basis services observedAt

                Ok(
                    RequesterState.NoRoles
                        {
                            Self = Actor.currentReference requester
                            InstallationId = services.InstallationId
                            BusinessDate = basis.BusinessDate
                            TimeZone = basis.TimeZoneId
                        }
                )
            | PrincipalAssociation.Current requester -> Ok(RequesterState.Active(active requester))

        state
        |> Result.map (fun state ->
            let scope =
                if RequesterState.offersOwnSessionTermination state then
                    let key =
                        match association with
                        | PrincipalAssociation.Current actor -> ActorId.format actor.ActorId
                        | _ -> PrincipalKey.naturalKey principal

                    Some
                        {
                            InstallationId = services.InstallationId
                            RequesterKey = key
                            ObservedAt = observedAt
                            RetirementCutoff = terminationCutoff
                        }
                else
                    None

            {
                ContextReply.Requester = state
                TerminationScope = scope
            })
        |> Result.map Outcome.Succeeded
        |> Result.defaultWith Outcome.Failed

    let context (services: ServiceContext) token =
        task {
            let! result =
                services.Store.Context
                    {
                        Principal = services.Principal
                        Credential = None
                        CancellationToken = token
                    }

            match result with
            | Error error -> return DisclosureReads.safeOutcome (QueryResults.failure error)
            | Ok facts ->
                let outcome =
                    contextResult
                        services
                        facts.ObservedAt
                        facts.Facts.Maintenance
                        facts.Facts.People
                        facts.Facts.AssignedRoles
                        facts.Association
                        facts.RequestPending
                        facts.CredentialUsable
                        facts.AuthorityVersion
                        facts.TerminationCutoff

                match outcome, facts.Association with
                | Outcome.Succeeded {
                                        Requester = RequesterState.Active _ | RequesterState.NoRoles _ | RequesterState.Disabled _
                                    },
                  PrincipalAssociation.Current actor ->
                    let evidence =
                        DisclosureReads.success
                            actor.ActorId
                            DisclosureKind.RequesterContext
                            None
                            None

                    return! DisclosureReads.release services token (outcome, Some evidence)
                | _ -> return DisclosureReads.safeContext outcome
        }
