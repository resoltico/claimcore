namespace ClaimCore.Hosting

open ClaimCore.Application
open ClaimCore.Postgres

/// Narrow actor-bound approval ports; schema-owner operations remain outside case-work Hosting.
module internal RuntimeActorApprovalStores =
    let signer (resources: RuntimeResources) =
        { new ICopySignerApprovalStore with
            member _.Approve(context, request, ct) =
                ManagedCopySignerApproval.approve
                    resources.DataSource
                    resources.Witness
                    context
                    request
                    ct
        }

    let deletion (resources: RuntimeResources) =
        { new ICopyDeletionApprovalStore with
            member _.Approve(context, request, ct) =
                ManagedCopyDeletionApproval.approve
                    resources.DataSource
                    resources.Witness
                    context
                    request
                    ct
        }

    let adoption (resources: RuntimeResources) =
        { new ICopyAdoptionApprovalStore with
            member _.Approve(context, request, ct) =
                ManagedCopyAdoptionApprovalWrite.approve
                    resources.DataSource
                    resources.Witness
                    context
                    request
                    ct
        }

    let handoff (resources: RuntimeResources) =
        { new IWriterHandoffApprovalStore with
            member _.Approve(context, request, ct) =
                WriterHandoffApproval.approve
                    resources.DataSource
                    resources.Witness
                    context
                    request
                    ct
        }

    let realDataActivation (resources: RuntimeResources) =
        { new IRealDataActivationApprovalStore with
            member _.Review(context, planId, ct) =
                RealDataActivationPlanReview.review
                    resources.DataSource
                    resources.Witness
                    context
                    planId
                    ct

            member _.Approve(context, request, ct) =
                RealDataActivationApproval.approve
                    resources.DataSource
                    resources.Witness
                    context
                    request
                    ct
        }
