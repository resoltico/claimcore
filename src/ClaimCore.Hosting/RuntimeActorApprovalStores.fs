namespace ClaimCore.Hosting

open ClaimCore.Application
open ClaimCore.Postgres

/// Narrow actor-bound approval ports; schema-owner operations remain outside case-work Hosting.
module internal RuntimeActorApprovalStores =
    let signer (resources: RuntimeResources) =
        { new ICopySignerApprovalStore with
            member _.Approve(context, request, _) =
                ManagedCopySignerApproval.approve
                    resources.DataSource
                    resources.Witness
                    context
                    request
        }

    let deletion (resources: RuntimeResources) =
        { new ICopyDeletionApprovalStore with
            member _.Approve(context, request, _) =
                ManagedCopyDeletionApproval.approve
                    resources.DataSource
                    resources.Witness
                    context
                    request
        }

    let adoption (resources: RuntimeResources) =
        { new ICopyAdoptionApprovalStore with
            member _.Approve(context, request, _) =
                ManagedCopyAdoptionApprovalWrite.approve
                    resources.DataSource
                    resources.Witness
                    context
                    request
        }

    let handoff (resources: RuntimeResources) =
        { new IWriterHandoffApprovalStore with
            member _.Approve(context, request, _) =
                WriterHandoffApproval.approve resources.DataSource resources.Witness context request
        }

    let realDataActivation (resources: RuntimeResources) =
        { new IRealDataActivationApprovalStore with
            member _.Review(context, planId, _) =
                RealDataActivationApproval.review
                    resources.DataSource
                    resources.Witness
                    context
                    planId

            member _.Approve(context, request, _) =
                RealDataActivationApproval.approve
                    resources.DataSource
                    resources.Witness
                    context
                    request
        }
