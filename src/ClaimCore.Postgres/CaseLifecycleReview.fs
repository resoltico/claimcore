namespace ClaimCore.Postgres

open System
open System.Data
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

module internal CaseLifecycleReview =
    let private summary historicalPayment (projection: LifecycleProjection) =
        let snapshot = Claim.view projection.Claim

        LifecycleReviewOutcome.Available
            {
                BusinessRevision = snapshot.Version
                LifecycleSequence = projection.Sequence
                LifecycleHash = Convert.ToHexStringLower projection.EventHash
                Disposition = CaseLifecycle.disposition projection.State
                PrivacyPhase = CaseLifecycle.privacy projection.State
                ActiveHolds =
                    CaseLifecycle.holds projection.State
                    |> List.map (fun hold ->
                        {
                            HoldId = hold.Id
                            ReviewOn = hold.ReviewOn
                        })
                VoidRequiresTwoApprovals = historicalPayment || snapshot.Fields.PaymentDate.IsSome
            }

    let private read dataSource (context: ActorCallContext) reference =
        task {
            use! connection = RuntimeDatabase.openConnectionAsync dataSource
            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision
                    connection
                    transaction
                    false
                    System.Threading.CancellationToken.None

            let! found = CaseLifecycleRead.lockProjection connection transaction reference

            match found with
            | None -> return LifecycleReviewOutcome.ResourceUnavailable
            | Some projection ->
                let! allowed =
                    CaseLifecycleStoreSupport.authorize
                        connection
                        transaction
                        revision
                        context
                        projection

                if not allowed then
                    return LifecycleReviewOutcome.ResourceUnavailable
                else
                    let! historicalPayment =
                        CaseLifecyclePaymentEvidence.historicalPayment
                            connection
                            transaction
                            projection.CaseId

                    return summary historicalPayment projection
        }

    let review dataSource (witness: WitnessProtocol) (context: ActorCallContext) reference =
        task {
            if
                context.Action <> EndpointAction.ReviewLifecycle
                || String.IsNullOrWhiteSpace reference
            then
                return LifecycleReviewOutcome.ResourceUnavailable
            else
                try
                    witness.Admit()
                    return! read dataSource context reference
                with
                | :? System.IO.InvalidDataException ->
                    return LifecycleReviewOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return LifecycleReviewOutcome.Failed CoreFault.StoreUnavailable
        }
