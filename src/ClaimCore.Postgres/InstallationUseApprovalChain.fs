namespace ClaimCore.Postgres

open System
open System.Threading
open ClaimCore.Application
open WitnessProtocolReconciliation

module internal InstallationUseApprovalChain =
    let private samePlan
        (one: RealDataActivationApprovalRequest)
        (two: RealDataActivationApprovalRequest)
        (plan: PublishedInstallationUsePlan)
        =
        one.PlanId = plan.PlanId
        && two.PlanId = plan.PlanId
        && one.ActivationId = plan.ActivationId
        && two.ActivationId = plan.ActivationId
        && one.ActivationPlanSha256 = Convert.FromHexString(plan.Plan.PlanSha256)
        && two.ActivationPlanSha256 = one.ActivationPlanSha256
        && one.PolicySha256 = Convert.FromHexString(plan.Plan.PolicySha256)
        && two.PolicySha256 = one.PolicySha256

    let private sameReview
        (one: RealDataActivationApprovalRequest)
        (two: RealDataActivationApprovalRequest)
        (plan: PublishedInstallationUsePlan)
        =
        one.ReviewWitnessSequence = two.ReviewWitnessSequence
        && one.ReviewWitnessHash = two.ReviewWitnessHash
        && one.ReviewWitnessSequence >= plan.SettlementSequence
        && one.ExpectedWitnessSequence = one.ReviewWitnessSequence
        && one.ExpectedWitnessHash = one.ReviewWitnessHash

    let private linked
        (a: InstallationUseApprovalEvidence)
        (b: InstallationUseApprovalEvidence)
        (two: RealDataActivationApprovalRequest)
        =
        a.ActorId <> b.ActorId
        && a.ApprovalId <> b.ApprovalId
        && two.ExpectedWitnessSequence = a.SettlementSequence
        && two.ExpectedWitnessHash = a.SettlementHash
        && b.IntentSequence = a.SettlementSequence + 1L

    let verify
        (one: RealDataActivationApprovalRequest)
        (two: RealDataActivationApprovalRequest)
        (a: InstallationUseApprovalEvidence)
        (b: InstallationUseApprovalEvidence)
        (plan: PublishedInstallationUsePlan)
        (witness: WitnessProtocol)
        (ct: CancellationToken)
        =
        task {
            do! witness.VerifyHistoricalTip(one.ReviewWitnessSequence, one.ReviewWitnessHash, ct)

            if
                not (samePlan one two plan)
                || not (sameReview one two plan)
                || not (linked a b two)
            then
                invalidOp "Two owner approvals do not form one reviewed chain."
        }
