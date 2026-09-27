/* Generated from ClaimCore.Contracts. Do not edit. */

export type WebV3AuthorityResponseByEndpoint = {
  readonly "authority.register": {
    readonly endpoint: "authority.register";
    readonly outcome:
      | {
          readonly tag: "APPLIED";
          readonly data: {
            readonly eventId: string;
            readonly grantRevision: string;
            readonly targetActorId: string;
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "UNCONFIRMED"; readonly data: { readonly eventId: string } };
  };
  readonly "authority.setGrant": {
    readonly endpoint: "authority.setGrant";
    readonly outcome:
      | {
          readonly tag: "APPLIED";
          readonly data: {
            readonly eventId: string;
            readonly grantRevision: string;
            readonly targetActorId: string;
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "UNCONFIRMED"; readonly data: { readonly eventId: string } };
  };
  readonly "authority.setEnabled": {
    readonly endpoint: "authority.setEnabled";
    readonly outcome:
      | {
          readonly tag: "APPLIED";
          readonly data: {
            readonly eventId: string;
            readonly grantRevision: string;
            readonly targetActorId: string;
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "UNCONFIRMED"; readonly data: { readonly eventId: string } };
  };
  readonly "authority.observe": {
    readonly endpoint: "authority.observe";
    readonly outcome:
      | {
          readonly tag: "APPLIED";
          readonly data: {
            readonly eventId: string;
            readonly grantRevision: string;
            readonly targetActorId: string;
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "UNCONFIRMED"; readonly data: { readonly eventId: string } };
  };
  readonly "authority.approveCopySigner": {
    readonly endpoint: "authority.approveCopySigner";
    readonly outcome:
      | {
          readonly tag: "APPROVED";
          readonly data: { readonly approvalId: string; readonly authorityRevision: string };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "STARTED_UNCONFIRMED"; readonly data: { readonly approvalId: string } };
  };
  readonly "authority.approveCopyDeletion": {
    readonly endpoint: "authority.approveCopyDeletion";
    readonly outcome:
      | {
          readonly tag: "APPROVED";
          readonly data: { readonly approvalId: string; readonly authorityRevision: string };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "STARTED_UNCONFIRMED"; readonly data: { readonly approvalId: string } };
  };
  readonly "authority.approveCopyAdoption": {
    readonly endpoint: "authority.approveCopyAdoption";
    readonly outcome:
      | {
          readonly tag: "APPROVED";
          readonly data: { readonly approvalId: string; readonly authorityRevision: string };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "STARTED_UNCONFIRMED"; readonly data: { readonly approvalId: string } };
  };
  readonly "authority.approveWriterHandoff": {
    readonly endpoint: "authority.approveWriterHandoff";
    readonly outcome:
      | {
          readonly tag: "APPROVED";
          readonly data: { readonly approvalId: string; readonly authorityRevision: string };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "STARTED_UNCONFIRMED"; readonly data: { readonly approvalId: string } };
  };
  readonly "authority.reviewRealDataActivation": {
    readonly endpoint: "authority.reviewRealDataActivation";
    readonly outcome:
      | {
          readonly tag: "REVIEWED";
          readonly data: {
            readonly planId: string;
            readonly activationId: string;
            readonly installationId: string;
            readonly lineageId: string;
            readonly epoch: string;
            readonly writerGeneration: string;
            readonly policySha256: string;
            readonly publicationRootSha256: string;
            readonly cycleId: string;
            readonly leaseId: string;
            readonly captureReceiptSha256: string;
            readonly primaryBaseCopyId: string;
            readonly witnessBaseCopyId: string;
            readonly primaryBasePhysicalReceiptSha256: string;
            readonly witnessBasePhysicalReceiptSha256: string;
            readonly checkpointObjectSha256: string;
            readonly testRestoreReportSha256: string;
            readonly testRestoreFullAuditSha256: string;
            readonly minimumArtifactCutoffSequence: string;
            readonly minimumPrimaryWalHorizon: string;
            readonly minimumWitnessWalHorizon: string;
            readonly canonicalPlan: string;
            readonly planSha256: string;
            readonly publishedAt: string;
            readonly publicationWitnessSequence: string;
            readonly publicationWitnessHash: string;
            readonly approvalExpiresNoLaterThan: string;
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null };
  };
  readonly "authority.approveRealDataActivation": {
    readonly endpoint: "authority.approveRealDataActivation";
    readonly outcome:
      | {
          readonly tag: "APPROVED";
          readonly data: { readonly approvalId: string; readonly authorityRevision: string };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "STARTED_UNCONFIRMED"; readonly data: { readonly approvalId: string } };
  };
};
