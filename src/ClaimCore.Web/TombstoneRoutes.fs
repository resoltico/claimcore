namespace ClaimCore.Web

open ClaimCore.Contracts

open System.Threading
open Microsoft.AspNetCore.Http
open ClaimCore.Application

/// Opaque post-purge steward workflow; no ordinary case reference or raw store reaches HTTP.
module TombstoneRoutes =
    let review admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        LifecycleRoutes.invoke
            admit
            maximumBytes
            HttpTombstoneInput.review
            (fun caseId -> core.Tombstones.Review(caseId, context.RequestAborted))
            WebWire.tombstoneReview
            context

    let approvePrune admit maximumBytes (core: IActorClaimsCore) context =
        LifecycleRoutes.invoke
            admit
            maximumBytes
            HttpTombstoneInput.approve
            (fun input ->
                core.Tombstones.ApproveWitnessPrune(
                    input.Proposal,
                    input.ApprovalId,
                    input.ExpiresAt,
                    CancellationToken.None
                ))
            (WebWire.tombstoneWrite "tombstone.approvePrune")
            context

    let approveTerminal admit maximumBytes (core: IActorClaimsCore) context =
        LifecycleRoutes.invoke
            admit
            maximumBytes
            HttpTombstoneTerminalInput.approve
            (fun input ->
                core.Tombstones.ApproveTerminal(
                    input.Proposal,
                    input.ApprovalId,
                    input.ExpiresAt,
                    CancellationToken.None
                ))
            (WebWire.tombstoneWrite "tombstone.approveTerminal")
            context

    let changeHold admit maximumBytes (core: IActorClaimsCore) context =
        LifecycleRoutes.invoke
            admit
            maximumBytes
            HttpTombstoneInput.hold
            (fun change -> core.Tombstones.ChangeHold(change, CancellationToken.None))
            (WebWire.tombstoneWrite "tombstone.changeHold")
            context
