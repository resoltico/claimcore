/* Generated from ClaimCore.Contracts. Do not edit. */
import type { Fault } from "./web-v3.types.core";

export type WebV3LifecycleResponseByEndpoint = {
  readonly "lifecycle.review": {
    readonly endpoint: "lifecycle.review";
    readonly outcome:
      | {
          readonly tag: "AVAILABLE";
          readonly data: {
            readonly businessRevision: string;
            readonly lifecycleSequence: string;
            readonly lifecycleHash: string;
            readonly disposition: "ACTIVE" | "VOIDED_DATA_ENTRY_ERROR";
            readonly privacyPhase:
              | "ACTIVE"
              | "ERASURE_REQUESTED"
              | "ERASURE_PENDING"
              | "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
              | "ERASURE_FINAL";
            readonly activeHolds: ReadonlyArray<{
              readonly holdId: string;
              readonly reviewOn: string;
            }>;
            readonly voidRequiresTwoApprovals: boolean;
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "CANCELLED"; readonly data: null }
      | { readonly tag: "FAILED"; readonly data: Fault };
  };
  readonly "lifecycle.apply": {
    readonly endpoint: "lifecycle.apply";
    readonly outcome:
      | {
          readonly tag: "APPLIED";
          readonly data: {
            readonly eventId: string;
            readonly businessRevision: string;
            readonly lifecycleSequence: string;
          };
        }
      | {
          readonly tag: "REFUSED";
          readonly data: {
            readonly reason:
              | "INVALID_IDENTITY"
              | "INVALID_REASON"
              | "INVALID_TIME"
              | "WRONG_CASE"
              | "VERSION_CONFLICT"
              | "REVISION_EXHAUSTED"
              | "WRONG_DISPOSITION"
              | "ERASURE_HAS_BEGUN"
              | "WRONG_PRIVACY_PHASE"
              | "DUPLICATE_HOLD"
              | "HOLD_NOT_FOUND"
              | "HOLD_ACTIVE"
              | "HOLD_CAPACITY_EXCEEDED"
              | "APPROVAL_REQUIRED"
              | "APPROVAL_CAPACITY_EXCEEDED"
              | "APPROVAL_MISMATCH"
              | "APPROVAL_EXPIRED"
              | "ERASURE_EVIDENCE_INCOMPLETE";
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "CANCELLED_BEFORE_ADMISSION"; readonly data: { readonly eventId: string } }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "UNCONFIRMED"; readonly data: { readonly eventId: string } };
  };
  readonly "lifecycle.approve": {
    readonly endpoint: "lifecycle.approve";
    readonly outcome:
      | {
          readonly tag: "APPLIED";
          readonly data: {
            readonly eventId: string;
            readonly businessRevision: string;
            readonly lifecycleSequence: string;
          };
        }
      | {
          readonly tag: "REFUSED";
          readonly data: {
            readonly reason:
              | "INVALID_IDENTITY"
              | "INVALID_REASON"
              | "INVALID_TIME"
              | "WRONG_CASE"
              | "VERSION_CONFLICT"
              | "REVISION_EXHAUSTED"
              | "WRONG_DISPOSITION"
              | "ERASURE_HAS_BEGUN"
              | "WRONG_PRIVACY_PHASE"
              | "DUPLICATE_HOLD"
              | "HOLD_NOT_FOUND"
              | "HOLD_ACTIVE"
              | "HOLD_CAPACITY_EXCEEDED"
              | "APPROVAL_REQUIRED"
              | "APPROVAL_CAPACITY_EXCEEDED"
              | "APPROVAL_MISMATCH"
              | "APPROVAL_EXPIRED"
              | "ERASURE_EVIDENCE_INCOMPLETE";
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "CANCELLED_BEFORE_ADMISSION"; readonly data: { readonly eventId: string } }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "UNCONFIRMED"; readonly data: { readonly eventId: string } };
  };
  readonly "tombstone.review": {
    readonly endpoint: "tombstone.review";
    readonly outcome:
      | {
          readonly tag: "AVAILABLE";
          readonly data: {
            readonly caseId: string;
            readonly purgeEventId: string;
            readonly purgeWitnessSequence: string;
            readonly purgeWitnessEpoch: string;
            readonly purgeWitnessHash: string;
            readonly cutoffSequence: string;
            readonly cutoffHash: string;
            readonly targetCount: string;
            readonly targetDigest: string;
            readonly privacyPhase:
              "ERASURE_PENDING" | "PAYLOAD_ERASED_SUPPRESSION_RETAINED" | "ERASURE_FINAL";
            readonly witnessPayloadPruned: boolean;
            readonly managedCopyCertificationPending: boolean;
            readonly authorityRevision: string;
            readonly authorityHash: string;
            readonly activeHolds: ReadonlyArray<{
              readonly holdId: string;
              readonly reviewOn: string;
            }>;
            readonly requiredDistinctStewardApprovals: 0 | 2;
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "CANCELLED"; readonly data: null }
      | { readonly tag: "FAILED"; readonly data: Fault };
  };
  readonly "tombstone.approvePrune": {
    readonly endpoint: "tombstone.approvePrune";
    readonly outcome:
      | {
          readonly tag: "APPLIED";
          readonly data: { readonly eventId: string; readonly authorityRevision: string };
        }
      | {
          readonly tag: "REFUSED";
          readonly data: {
            readonly reason:
              | "INVALID_IDENTITY"
              | "INVALID_REASON"
              | "INVALID_TIME"
              | "WRONG_CASE"
              | "VERSION_CONFLICT"
              | "REVISION_EXHAUSTED"
              | "WRONG_DISPOSITION"
              | "ERASURE_HAS_BEGUN"
              | "WRONG_PRIVACY_PHASE"
              | "DUPLICATE_HOLD"
              | "HOLD_NOT_FOUND"
              | "HOLD_ACTIVE"
              | "HOLD_CAPACITY_EXCEEDED"
              | "APPROVAL_REQUIRED"
              | "APPROVAL_CAPACITY_EXCEEDED"
              | "APPROVAL_MISMATCH"
              | "APPROVAL_EXPIRED"
              | "ERASURE_EVIDENCE_INCOMPLETE";
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "CANCELLED_BEFORE_ADMISSION"; readonly data: { readonly eventId: string } }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "UNCONFIRMED"; readonly data: { readonly eventId: string } };
  };
  readonly "tombstone.approveTerminal": {
    readonly endpoint: "tombstone.approveTerminal";
    readonly outcome:
      | {
          readonly tag: "APPLIED";
          readonly data: { readonly eventId: string; readonly authorityRevision: string };
        }
      | {
          readonly tag: "REFUSED";
          readonly data: {
            readonly reason:
              | "INVALID_IDENTITY"
              | "INVALID_REASON"
              | "INVALID_TIME"
              | "WRONG_CASE"
              | "VERSION_CONFLICT"
              | "REVISION_EXHAUSTED"
              | "WRONG_DISPOSITION"
              | "ERASURE_HAS_BEGUN"
              | "WRONG_PRIVACY_PHASE"
              | "DUPLICATE_HOLD"
              | "HOLD_NOT_FOUND"
              | "HOLD_ACTIVE"
              | "HOLD_CAPACITY_EXCEEDED"
              | "APPROVAL_REQUIRED"
              | "APPROVAL_CAPACITY_EXCEEDED"
              | "APPROVAL_MISMATCH"
              | "APPROVAL_EXPIRED"
              | "ERASURE_EVIDENCE_INCOMPLETE";
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "CANCELLED_BEFORE_ADMISSION"; readonly data: { readonly eventId: string } }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "UNCONFIRMED"; readonly data: { readonly eventId: string } };
  };
  readonly "tombstone.changeHold": {
    readonly endpoint: "tombstone.changeHold";
    readonly outcome:
      | {
          readonly tag: "APPLIED";
          readonly data: { readonly eventId: string; readonly authorityRevision: string };
        }
      | {
          readonly tag: "REFUSED";
          readonly data: {
            readonly reason:
              | "INVALID_IDENTITY"
              | "INVALID_REASON"
              | "INVALID_TIME"
              | "WRONG_CASE"
              | "VERSION_CONFLICT"
              | "REVISION_EXHAUSTED"
              | "WRONG_DISPOSITION"
              | "ERASURE_HAS_BEGUN"
              | "WRONG_PRIVACY_PHASE"
              | "DUPLICATE_HOLD"
              | "HOLD_NOT_FOUND"
              | "HOLD_ACTIVE"
              | "HOLD_CAPACITY_EXCEEDED"
              | "APPROVAL_REQUIRED"
              | "APPROVAL_CAPACITY_EXCEEDED"
              | "APPROVAL_MISMATCH"
              | "APPROVAL_EXPIRED"
              | "ERASURE_EVIDENCE_INCOMPLETE";
          };
        }
      | { readonly tag: "RESOURCE_UNAVAILABLE"; readonly data: null }
      | { readonly tag: "CANCELLED_BEFORE_ADMISSION"; readonly data: { readonly eventId: string } }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "UNCONFIRMED"; readonly data: { readonly eventId: string } };
  };
};
