import {
  deliveryBusy,
  deliveryEditable,
  sameRequest,
  deliveryCompleting,
  invalidateDelivery,
  deliveryStatus,
} from "./operationDelivery";
import {
  ownsDisclosure,
  invalidateResource,
  beginCaseRead,
  beginConflictRead,
  observeCase,
  observeConflict,
} from "./operationDisclosure";
import { sameOwnerScope } from "../protocol/ownerScope";
import { editDraft, editDraftFields, captureDraftDate, clearDraftDates } from "./operationDraft";
import {
  conflictFromRefusal,
  sameConflictTarget,
  rebasedOperationBinding,
} from "./operationConflict";
import { operationEntry } from "../protocol/operationCatalog";
import { taskForAct } from "../protocol/taskCatalog";
import { inputErrors } from "./inputErrors";
import {
  operationSource,
  type EffectIdentity,
  type OperationPhase,
  type OperationEffect,
  type OperationState,
  type OperationAction,
} from "./operationTypes";
export type {
  EffectIdentity,
  OperationEffect,
  OperationState,
  OperationAction,
} from "./operationTypes";
const operationBusy = (state: OperationState) => deliveryBusy(state.delivery);
const canEdit = (state: OperationState) => deliveryEditable(state.delivery);
const current = (state: OperationState, identity: EffectIdentity): boolean =>
  sameOwnerScope(state.delivery.owner, identity.owner) && ownsDisclosure(state.disclosed, identity);
const sameEffect = sameRequest;
const completing = (state: OperationState, identity: EffectIdentity) =>
  deliveryCompleting(state.delivery, identity, current(state, identity));
const append = (state: OperationState, effect: OperationEffect): OperationState => ({
  ...state,
  outbox: [...state.outbox, effect],
});
const localSource = operationSource;
const uncertain = (phase: OperationPhase): boolean => phase.kind === "UNRESOLVED";
const authored = (state: OperationState, draft: OperationState["draft"]): OperationState => {
  if (draft === state.draft) {
    return state;
  }
  if (draft.generation === state.draft.generation) {
    return { ...state, draft };
  }
  return {
    ...state,
    draft,
    delivery: { ...state.delivery, phase: { kind: "EDITING", exposed: null } },
    disclosed: { ...state.disclosed, review: null, notice: null, fieldErrors: {}, rejection: null },
  };
};
const disclosureRevision = (state: OperationState, refused: boolean) =>
  state.disclosed.revision + (refused ? 1 : 0);
const invalidate = (state: OperationState, refused: boolean): OperationState => ({
  ...state,
  outbox: [],
  delivery: invalidateDelivery(state.delivery),
  disclosed: invalidateResource(state.disclosed, refused),
});
const preview = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "PREVIEW";
    }
  >,
): OperationState => {
  if (
    !canEdit(state) ||
    state.disclosed.conflict !== null ||
    !current(state, action.identity) ||
    state.draft.generation !== action.generation ||
    state.disclosed.read !== null
  ) {
    return state;
  }
  const { source } = action;
  return append(
    {
      ...state,
      delivery: {
        ...state.delivery,
        notice: null,
        phase: {
          kind: "PREVIEWING",
          source,
          effect: action.identity,
        },
      },
      disclosed: {
        ...state.disclosed,
        notice: null,
        fieldErrors: {},
        rejection: null,
      },
    },
    {
      kind: "PREVIEW",
      identity: action.identity,
      source,
    },
  );
};
const reviewedForExecution = (state: OperationState): boolean => {
  const { phase } = state.delivery;
  return (
    phase.kind === "REVIEWING" &&
    phase.confirmed &&
    state.disclosed.review !== null &&
    sameEffect(phase.reviewIdentity, state.disclosed.review.identity) &&
    !state.disclosed.refused &&
    state.disclosed.confirmedContext === state.disclosed.context
  );
};
const execute = (state: OperationState, identity: EffectIdentity): OperationState => {
  const { phase } = state.delivery;
  if (!current(state, identity)) {
    return state;
  }
  const reviewed = reviewedForExecution(state);
  if (operationBusy(state) || (phase.kind !== "UNRESOLVED" && !reviewed)) {
    return state;
  }
  const source = operationSource(phase);
  if (source === null) {
    return state;
  }
  return append(
    {
      ...state,
      delivery: {
        ...state.delivery,
        notice: null,
        phase:
          phase.kind === "UNRESOLVED"
            ? {
                ...phase,
                pending: {
                  kind: "EXECUTE",
                  identity,
                },
              }
            : {
                kind: "PREPARING",
                source,
                effect: identity,
                reviewIdentity: phase.kind === "REVIEWING" ? phase.reviewIdentity : null,
              },
      },
      disclosed: {
        ...state.disclosed,
        notice: null,
      },
    },
    {
      kind: "EXECUTE",
      identity,
      source,
    },
  );
};
const readConflict = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "READ_CONFLICT";
    }
  >,
) => {
  const snapshot = state.disclosed.conflict;
  if (
    snapshot === null ||
    snapshot.identity.id !== action.conflictId ||
    !current(state, action.identity) ||
    operationBusy(state) ||
    uncertain(state.delivery.phase) ||
    state.disclosed.read !== null
  ) {
    return state;
  }
  return append(
    {
      ...state,
      disclosed: beginConflictRead(state.disclosed, action.identity, snapshot),
    },
    {
      kind: "READ_CONFLICT",
      identity: action.identity,
      conflictId: snapshot.identity.id,
      source: snapshot.source,
      minimumRevision: snapshot.minimumRevision,
    },
  );
};
const conflictObservation = (
  state: OperationState,
  action: Extract<OperationAction, { type: "CONFLICT_OBSERVED" | "CONFLICT_READ_FAILED" }>,
): OperationState => {
  if (!current(state, action.identity)) {
    return state;
  }
  const disclosed = observeConflict(state.disclosed, action);
  return disclosed === state.disclosed ? state : { ...state, disclosed };
};
const reviewLatest = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "REVIEW_LATEST";
    }
  >,
): OperationState => {
  const snapshot = state.disclosed.conflict;
  if (
    snapshot === null ||
    snapshot.identity.id !== action.conflictId ||
    snapshot.latest === null ||
    snapshot.latest.revision === null ||
    !canEdit(state) ||
    !current(state, action.identity) ||
    state.draft.generation !== action.generation ||
    state.disclosed.read !== null ||
    state.disclosed.refused
  ) {
    return state;
  }
  if (!sameConflictTarget(snapshot.source.prepared.input, action.source.prepared.input)) {
    return state;
  }
  const { source } = action;
  return append(
    {
      ...state,
      draft: {
        ...state.draft,
        values: action.values,
        binding: rebasedOperationBinding(action.source.prepared.input, snapshot.latest.revision),
        generation: state.draft.generation + 1,
      },
      disclosed: {
        ...state.disclosed,
        conflict: null,
        notice: null,
        fieldErrors: {},
        rejection: null,
      },
      delivery: {
        ...state.delivery,
        phase: {
          kind: "PREVIEWING",
          source,
          effect: action.identity,
        },
      },
    },
    {
      kind: "PREVIEW",
      source,
      identity: action.identity,
    },
  );
};
type RefuseAction = Extract<
  OperationAction,
  {
    type: "REFUSE";
  }
>;
const refusedPhase = (
  retainUncertainty: boolean,
  source: ReturnType<typeof operationSource>,
): OperationPhase =>
  retainUncertainty && source !== null
    ? {
        kind: "UNRESOLVED",
        source,
        pending: null,
      }
    : {
        kind: "EDITING",
        exposed: source,
      };
const canPublishRefusal = (state: OperationState, action: RefuseAction) =>
  action.identity === undefined ||
  (current(state, action.identity) &&
    state.disclosed.confirmedContext === state.disclosed.context &&
    !state.disclosed.refused);
const refusalFeedback = (
  state: OperationState,
  action: RefuseAction,
  source: ReturnType<typeof operationSource>,
  sticky: boolean,
) => {
  const publish = canPublishRefusal(state, action);
  return {
    notice: publish && deliveryStatus(action.notice) === null ? action.notice : null,
    fieldErrors: publish
      ? inputErrors(
          action.rejection,
          taskForAct(state.draft.entry.subject, state.draft.entry.kind),
          source?.submittedRows ?? {},
          action.target,
          action.notice,
        )
      : {},
    rejection: publish ? (action.rejection ?? null) : null,
    conflict: publish
      ? (conflictFromRefusal(action, source, operationEntry(state.draft.entry), sticky) ??
        state.disclosed.conflict)
      : null,
  };
};
const refuse = (state: OperationState, action: RefuseAction): OperationState => {
  if (action.identity !== undefined && !completing(state, action.identity)) {
    return state;
  }
  const source = operationSource(state.delivery.phase);
  const sticky = uncertain(state.delivery.phase) || action.uncertain;
  const next = {
    ...state,
    delivery: {
      ...state.delivery,
      phase: refusedPhase(sticky, source),
      notice: deliveryStatus(action.notice),
    },
    disclosed: {
      ...state.disclosed,
      ...refusalFeedback(state, action, source, sticky),
      refused: state.disclosed.refused || action.disclosure,
      revision: disclosureRevision(state, action.disclosure),
    },
  };
  if (next.disclosed.conflict !== null && next.disclosed.conflict !== state.disclosed.conflict) {
    return readConflict(next, {
      type: "READ_CONFLICT",
      identity: next.disclosed.conflict.identity,
      conflictId: next.disclosed.conflict.identity.id,
    });
  }
  return action.disclosure && action.identity !== undefined
    ? append(next, {
        kind: "DISCLOSURE_REFUSED",
        identity: action.identity,
        notice: action.notice,
      })
    : next;
};
const record = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "RECORD";
    }
  >,
): OperationState => {
  if (state.delivery.phase.kind === "PREPARING" || !completing(state, action.identity)) {
    return state;
  }
  const source = operationSource(state.delivery.phase);
  if (source === null) {
    return state;
  }
  const publish =
    current(state, action.identity) &&
    state.disclosed.confirmedContext === state.disclosed.context &&
    !state.disclosed.refused;
  const accepted: OperationState = {
    ...state,
    delivery: {
      ...state.delivery,
      notice: null,
      phase: {
        kind: "ACCEPTED",
        source,
      },
    },
    draft: {
      ...state.draft,
      pendingDiscard: null,
      dateEntries: {},
    },
    disclosed: {
      ...state.disclosed,
      notice: null,
      receipt: publish ? action.receipt : null,
      confirmedContext: publish ? state.disclosed.context : state.disclosed.confirmedContext,
      refused: state.disclosed.refused,
    },
  };
  return publish
    ? append(accepted, {
        kind: "RECORDED",
        identity: action.identity,
      })
    : accepted;
};
const reviewed = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "REVIEW";
    }
  >,
): OperationState => {
  if (!completing(state, action.identity) || state.delivery.phase.kind !== "PREVIEWING") {
    return state;
  }
  return {
    ...state,
    delivery: {
      ...state.delivery,
      phase: {
        kind: "REVIEWING",
        source: state.delivery.phase.source,
        reviewIdentity: action.identity,
        confirmed: false,
      },
    },
    disclosed: {
      ...state.disclosed,
      review: { identity: action.identity, value: action.review },
      confirmedContext: state.disclosed.context,
      refused: false,
    },
  };
};
const contextChanged = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "CONTEXT";
    }
  >,
): OperationState => {
  if (
    state.disclosed.context.credential === action.credential &&
    state.disclosed.context.target === action.target &&
    state.disclosed.context.epoch === action.epoch &&
    sameOwnerScope(state.disclosed.context.owner, action.owner)
  ) {
    return state;
  }
  const next = invalidate(state, false);
  return {
    ...next,
    draft: { ...next.draft, owner: state.draft.owner ?? action.owner },
    delivery: {
      ...next.delivery,
      owner: state.delivery.owner ?? action.owner,
      credential: state.delivery.owner === null ? action.credential : state.delivery.credential,
    },
    disclosed: {
      ...next.disclosed,
      conflict: null,
      context: {
        owner: action.owner,
        generation: state.disclosed.context.generation + 1,
        epoch: action.epoch,
        credential: action.credential,
        target: action.target,
      },
    },
  };
};
const returned = (state: OperationState): OperationState =>
  canEdit(state)
    ? {
        ...state,
        delivery: {
          ...state.delivery,
          phase: {
            kind: "EDITING",
            exposed: localSource(state.delivery.phase),
          },
        },
      }
    : state;
const confirmed = (state: OperationState, value: boolean): OperationState =>
  state.delivery.phase.kind === "REVIEWING" && !state.disclosed.refused
    ? {
        ...state,
        delivery: {
          ...state.delivery,
          phase: {
            ...state.delivery.phase,
            confirmed: value,
          },
        },
      }
    : state;
const reset = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "RESET";
    }
  >,
): OperationState =>
  operationBusy(state) || (uncertain(state.delivery.phase) && !action.abandon)
    ? state
    : {
        ...action.state,
        delivery: {
          ...action.state.delivery,
          owner: state.delivery.owner ?? action.state.delivery.owner,
          credential:
            state.delivery.owner === null
              ? action.state.delivery.credential
              : state.delivery.credential,
        },
        draft: {
          ...action.state.draft,
          owner: state.draft.owner ?? action.state.draft.owner,
          dateEntries: {},
        },
        disclosed: {
          ...action.state.disclosed,
          revision: state.disclosed.revision + 1,
          refused: state.disclosed.refused || action.state.disclosed.refused,
          context: state.disclosed.context,
          confirmedContext: state.disclosed.confirmedContext,
        },
      };
const checkingStatus = (state: OperationState, identity: EffectIdentity): OperationState =>
  state.delivery.phase.kind === "UNRESOLVED" && !operationBusy(state) && current(state, identity)
    ? append(
        {
          ...state,
          delivery: {
            ...state.delivery,
            phase: {
              ...state.delivery.phase,
              pending: {
                kind: "LOOKUP",
                identity,
              },
            },
          },
        },
        {
          kind: "CHECK_OPERATION",
          identity,
          source: state.delivery.phase.source,
        },
      )
    : state;
const notAccepted = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "NOT_ACCEPTED";
    }
  >,
): OperationState =>
  completing(state, action.identity)
    ? {
        ...state,
        delivery: {
          ...state.delivery,
          phase: { kind: "EDITING", exposed: null },
          notice: { kind: "local", reason: "notAccepted" },
        },
      }
    : state;
const stillUnresolved = (
  state: OperationState,
  identity: EffectIdentity,
  finalAt: string,
): OperationState =>
  completing(state, identity) && state.delivery.phase.kind === "UNRESOLVED"
    ? {
        ...state,
        delivery: {
          ...state.delivery,
          phase: {
            ...state.delivery.phase,
            pending: null,
          },
          notice: { kind: "pending", finalAt },
        },
      }
    : state;
const readCurrent = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "READ_CURRENT";
    }
  >,
): OperationState =>
  current(state, action.identity) && !operationBusy(state) && state.disclosed.read === null
    ? append(
        {
          ...state,
          disclosed: beginCaseRead(state.disclosed, action.identity, action.caseId),
        },
        {
          kind: "READ_CURRENT",
          identity: action.identity,
          caseId: action.caseId,
          reference: action.reference,
        },
      )
    : state;
const disclosed = (
  state: OperationState,
  action: Extract<OperationAction, { type: "DISCLOSED" | "DISCLOSURE_READ_FAILED" }>,
): OperationState => {
  if (!current(state, action.identity)) {
    return state;
  }
  const resource = observeCase(state.disclosed, action);
  return resource === state.disclosed ? state : { ...state, disclosed: resource };
};
type NavigationAction = Exclude<
  OperationAction,
  {
    type:
      | "EDIT"
      | "DATE_ENTRY"
      | "CLEAR_DATE_ENTRIES"
      | "EDIT_FIELDS"
      | "READ_CURRENT"
      | "RESTORE_CREATION_DRAFT"
      | "DISCLOSED"
      | "DISCLOSURE_READ_FAILED"
      | "READ_CONFLICT"
      | "CONFLICT_OBSERVED"
      | "CONFLICT_READ_FAILED"
      | "REVIEW_LATEST"
      | "PREVIEW"
      | "EXECUTE"
      | "REFUSE"
      | "RECORD"
      | "REVIEW"
      | "HANDOFF"
      | "OPERATION_PENDING";
  }
>;
const navigation = (state: OperationState, action: NavigationAction): OperationState => {
  switch (action.type) {
    case "CONTEXT":
      return contextChanged(state, action);
    case "INVALIDATE":
      return invalidate(state, action.refused);
    case "RESTORE":
      return {
        ...state,
        disclosed: {
          ...state.disclosed,
          refused: false,
        },
      };
    case "RETURN":
      return returned(state);
    case "CONFIRM":
      return confirmed(state, action.value);
    case "DISCARD":
      return operationBusy(state)
        ? state
        : {
            ...state,
            draft: {
              ...state.draft,
              pendingDiscard: action.kind,
            },
          };
    case "RESET":
      return reset(state, action);
    case "CHECK_OPERATION":
      return checkingStatus(state, action.identity);
    case "NOT_ACCEPTED":
      return notAccepted(state, action);
    case "EFFECT_STARTED":
      return {
        ...state,
        outbox: state.outbox.filter((effect) => effect.identity.id !== action.id),
      };
  }
};
const handoff = (state: OperationState, identity: EffectIdentity): OperationState =>
  completing(state, identity) && state.delivery.phase.kind === "PREPARING"
    ? {
        ...state,
        delivery: {
          ...state.delivery,
          phase: {
            ...state.delivery.phase,
            kind: "SUBMITTING",
          },
        },
      }
    : state;
export const canRestoreCreationDraft = (state: OperationState): boolean => {
  const previous = state.disclosed.confirmedContext;
  const next = state.disclosed.context;
  const masked = previous !== next || state.disclosed.refused;
  return (
    masked &&
    previous.target === next.target &&
    state.draft.target === next.target &&
    state.draft.entry.subject === "CASE" &&
    state.draft.entry.kind === "case.open" &&
    state.disclosed.caseObservation === null &&
    state.delivery.phase.kind === "EDITING"
  );
};
// Reject a scheduled restore if a refusal or another phase transition intervened.
const restoreCreationDraft = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "RESTORE_CREATION_DRAFT";
    }
  >,
): OperationState =>
  current(state, action.identity) &&
  state.delivery.phase === action.editing &&
  state.disclosed.revision === action.disclosureRevision &&
  canRestoreCreationDraft(state)
    ? {
        ...state,
        disclosed: {
          ...state.disclosed,
          confirmedContext: state.disclosed.context,
          refused: false,
        },
      }
    : state;
type DisclosureAction = Extract<
  OperationAction,
  {
    type: "READ_CURRENT" | "RESTORE_CREATION_DRAFT" | "DISCLOSED" | "DISCLOSURE_READ_FAILED";
  }
>;
const isDisclosureAction = (action: OperationAction): action is DisclosureAction =>
  action.type === "READ_CURRENT" ||
  action.type === "RESTORE_CREATION_DRAFT" ||
  action.type === "DISCLOSED" ||
  action.type === "DISCLOSURE_READ_FAILED";
const disclosureTransition = (state: OperationState, action: DisclosureAction): OperationState => {
  if (action.type === "RESTORE_CREATION_DRAFT") {
    return restoreCreationDraft(state, action);
  }
  return action.type === "READ_CURRENT" ? readCurrent(state, action) : disclosed(state, action);
};
type ConflictAction = Extract<
  OperationAction,
  {
    type: "READ_CONFLICT" | "CONFLICT_OBSERVED" | "CONFLICT_READ_FAILED" | "REVIEW_LATEST";
  }
>;
const isConflictAction = (action: OperationAction): action is ConflictAction =>
  ["READ_CONFLICT", "CONFLICT_OBSERVED", "CONFLICT_READ_FAILED", "REVIEW_LATEST"].includes(
    action.type,
  );
const conflictTransition = (state: OperationState, action: ConflictAction): OperationState => {
  switch (action.type) {
    case "READ_CONFLICT":
      return readConflict(state, action);
    case "CONFLICT_OBSERVED":
    case "CONFLICT_READ_FAILED":
      return conflictObservation(state, action);
    case "REVIEW_LATEST":
      return reviewLatest(state, action);
  }
};
type FormEntryAction = Extract<
  OperationAction,
  { type: "EDIT" | "EDIT_FIELDS" | "DATE_ENTRY" | "CLEAR_DATE_ENTRIES" }
>;
const isFormEntryAction = (action: OperationAction): action is FormEntryAction =>
  ["EDIT", "EDIT_FIELDS", "DATE_ENTRY", "CLEAR_DATE_ENTRIES"].includes(action.type);
const formEntryTransition = (state: OperationState, action: FormEntryAction): OperationState => {
  if (action.type === "CLEAR_DATE_ENTRIES") {
    return authored(state, clearDraftDates(state.draft));
  }
  if (!canEdit(state)) {
    return state;
  }
  if (action.type === "DATE_ENTRY") {
    const value = state.draft.values[action.path];
    const canonical = typeof value === "string" ? value : "";
    if (
      state.disclosed.refused ||
      state.disclosed.confirmedContext !== state.disclosed.context ||
      canonical !== action.canonical
    ) {
      return state;
    }
    return authored(state, captureDraftDate(state.draft, action));
  }
  return authored(
    state,
    action.type === "EDIT" ? editDraft(state.draft, action) : editDraftFields(state.draft, action),
  );
};
/** Reads publish only within their current context; dispatched delivery settles its original effect identity. */
export const operationReducer = (
  state: OperationState,
  action: OperationAction,
): OperationState => {
  if (isFormEntryAction(action)) {
    return formEntryTransition(state, action);
  }
  if (isConflictAction(action)) {
    return conflictTransition(state, action);
  }
  if (isDisclosureAction(action)) {
    return disclosureTransition(state, action);
  }
  switch (action.type) {
    case "PREVIEW":
      return preview(state, action);
    case "EXECUTE":
      return execute(state, action.identity);
    case "HANDOFF":
      return handoff(state, action.identity);
    case "REFUSE":
      return refuse(state, action);
    case "RECORD":
      return record(state, action);
    case "OPERATION_PENDING":
      return stillUnresolved(state, action.identity, action.finalAt);
    case "REVIEW":
      return reviewed(state, action);
    default:
      return navigation(state, action);
  }
};
const disclosedReview = (state: OperationState) => {
  const { phase } = state.delivery;
  const reviewIdentity =
    phase.kind === "REVIEWING" || phase.kind === "PREPARING" || phase.kind === "SUBMITTING"
      ? phase.reviewIdentity
      : null;
  const review =
    reviewIdentity !== null &&
    state.disclosed.review !== null &&
    sameEffect(reviewIdentity, state.disclosed.review.identity)
      ? state.disclosed.review.value
      : null;
  return review;
};
const phaseConfirmed = (phase: OperationPhase): boolean =>
  phase.kind === "REVIEWING"
    ? phase.confirmed
    : (phase.kind === "PREPARING" || phase.kind === "SUBMITTING") && phase.reviewIdentity !== null;
export const operationView = (state: OperationState) => {
  const { phase } = state.delivery;
  const source = operationSource(phase);
  const review = disclosedReview(state);
  return {
    entry: state.draft.entry,
    target: state.draft.target,
    values: state.draft.values,
    dateEntries: state.draft.dateEntries,
    notice: state.disclosed.notice ?? state.delivery.notice,
    fieldErrors: state.disclosed.fieldErrors,
    rejection: state.disclosed.rejection,
    conflict: state.disclosed.conflict,
    pendingDiscard: state.draft.pendingDiscard,
    disclosureRefused: state.disclosed.refused,
    phase: phase.kind,
    busy: operationBusy(state),
    priorUncertain: uncertain(phase),
    prepared: source?.prepared ?? null,
    review,
    receipt: phase.kind === "ACCEPTED" ? state.disclosed.receipt : null,
    confirmed: phaseConfirmed(phase),
  };
};
