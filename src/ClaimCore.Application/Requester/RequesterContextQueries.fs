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
        policy
        (maintenance: RegisterMaintenance)
        (actor: AccessView)
        (roles: ActorRoleSummary list)
        =
        let principal = services.Principal

        let active (requester: Actor) : ActorContext =
            let _, issuer, _ = PrincipalKey.parts principal

            {
                Issuer = issuer
                Roles = roles |> ActorRoleSummary.ordered
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
                        Advice.installationOffers policy principal actor
                Capability =
                    Capability.all
                    |> List.filter (fun capability ->
                        Access.can principal actor capability Scope.Installation)
            }

        let state =
            match actor.Actor with
            | None ->
                let _, issuer, subject = PrincipalKey.parts principal

                Ok(
                    RequesterState.NotRegistered
                        {
                            Issuer = issuer
                            Subject = subject
                            Claims = services.Claims
                        }
                )
            | Some requester when requester.Principal <> principal ->
                Error CoreFault.StoreIntegrityError
            | Some requester when
                AccountState.classify requester.Enabled actor.HasAssignedRoles =
                    AccountState.Disabled
                ->
                Ok(RequesterState.Disabled(Actor.currentReference requester))
            | Some requester when
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
            | Some requester -> Ok(RequesterState.Active(active requester))

        state
        |> Result.map (fun state -> { ContextReply.Requester = state })
        |> Result.map Outcome.Succeeded
        |> Result.defaultWith Outcome.Failed

    let context (services: ServiceContext) token =
        task {
            let! result =
                services.Store.Context
                    {
                        Principal = services.Principal
                        Claims = services.Claims
                        CancellationToken = token
                    }

            return
                result
                |> Result.map (fun facts ->
                    contextResult
                        services
                        facts.ObservedAt
                        facts.CaseReferencePolicy
                        facts.Facts.Maintenance
                        facts.Facts.People
                        facts.Facts.AssignedRoles)
                |> readResult
        }
