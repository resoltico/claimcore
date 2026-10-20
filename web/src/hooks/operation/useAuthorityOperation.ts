import { prepareRequest, ProtocolInputError } from "../../protocol/preparedRequest";
import { useEffect, useState, useRef } from "react";
import { useMutationLifecycle } from "./useMutationLifecycle";
import { buildRequest, wireSchemas } from "../../protocol/client";
import { service, resultNotice } from "../../api/service";
import { localNotice, type Notice } from "../../api/notices";
import { formValue, type FormValues } from "../../protocol/forms";
import { attemptKnowledge, resolutionEvidence } from "../../protocol/delivery";
import { isDisclosureRefused } from "../../api/outcomes";
import { useAuthorityDisclosure } from "./useAuthorityDisclosure";
export const authorityDocument = wireSchemas["authority.execute"].request;
const useAuthorityState = () => {
  const [values, setValues] = useState<FormValues>({ operationId: crypto.randomUUID() });
  const delivery = useMutationLifecycle<"authority.execute">();
  const [fieldError, setFieldError] = useState<string | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [pendingDiscard, setPendingDiscard] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<string | null>(null);
  return {
    ...delivery,
    fieldError,
    setFieldError,
    pendingDiscard,
    setPendingDiscard,
    values,
    setValues,
    confirmed,
    setConfirmed,
    notice,
    setNotice,
    receipt,
    setReceipt,
  };
};
type State = ReturnType<typeof useAuthorityState>;
const reviewAuthority = (state: State, fallback: string) => {
  try {
    const values = { ...state.values, "act.kind": state.values["act.kind"] ?? fallback };
    state.setPrepared(
      prepareRequest(
        "authority.execute",
        buildRequest(
          "authority.execute",
          formValue(authorityDocument, authorityDocument, "", values),
        ),
      ),
    );
    state.setConfirmed(false);
    state.setNotice(null);
  } catch (error) {
    state.setFieldError(error instanceof ProtocolInputError ? error.path.join(".") : null);
    state.setNotice(localNotice("invalidInput"));
  }
};
type DeliveryControl = { busy: boolean; serial: number; dispatched: boolean; token: string };
const executeAuthority = async (
  state: State,
  token: string,
  control: DeliveryControl,
  invalidateDisclosure: (force: boolean) => void,
) => {
  if (state.prepared === null || control.busy || (!state.confirmed && !state.uncertain)) {
    return;
  }
  control.busy = true;
  control.dispatched = true;
  const serial = ++control.serial;
  state.begin(serial);
  const result = await service.authorityExecute(state.prepared, token);
  if (control.serial !== serial) {
    return;
  }
  control.busy = false;
  control.dispatched = false;
  state.complete(serial, attemptKnowledge("authority.execute", result), resolutionEvidence(result));
  if (isDisclosureRefused(result)) {
    invalidateDisclosure(true);
  }
  if (
    result.kind === "outcome" &&
    (result.value.outcome.tag === "ACCEPTED" || result.value.outcome.tag === "ALREADY_ACCEPTED")
  ) {
    state.setValues({ operationId: crypto.randomUUID() });
    state.setReceipt(result.value.outcome.data.operationId);
    state.setNotice(null);
    invalidateDisclosure(true);
    return;
  }
  if (result.kind === "hostFailure" && result.failure.diagnostic.id === "PRINCIPAL_INPUT_INVALID") {
    state.setFieldError(`target.${result.failure.diagnostic.parameters.target}`);
  }
  state.setNotice(resultNotice(result));
};
const authorityDraftActions = (state: State) => {
  const discard = () => {
    if (state.uncertain || state.busy) {
      return;
    }
    state.setValues({
      operationId: crypto.randomUUID(),
      ...(state.pendingDiscard !== null && state.pendingDiscard !== ""
        ? { "act.kind": state.pendingDiscard }
        : {}),
    });
    state.clearPrepared();
    state.setPendingDiscard(null);
  };
  const changeCommand = (next: string) => {
    if (Object.keys(state.values).length > 1) {
      state.setPendingDiscard(next);
    } else {
      state.setValues({ operationId: crypto.randomUUID(), "act.kind": next });
    }
  };
  return { discard, changeCommand };
};
const useAuthorityLock = (state: State, onLockChange: (locked: boolean) => void) => {
  useEffect(() => {
    onLockChange(state.bytes !== null || state.busy || Object.keys(state.values).length > 1);
    return () => {
      onLockChange(false);
    };
  }, [state.bytes, state.busy, state.values, onLockChange]);
};
export const useAuthorityOperation = (
  token: string,
  fallback: string,
  onLockChange: (locked: boolean) => void,
) => {
  const state = useAuthorityState();
  const disclosure = useAuthorityDisclosure();
  const delivery = useRef<DeliveryControl>({ busy: false, serial: 0, dispatched: false, token });
  const { interrupt, setReceipt } = state;
  const { invalidate } = disclosure;
  useEffect(() => {
    const control = delivery.current;
    if (control.token !== token) {
      control.token = token;
      invalidate(true);
      interrupt();
      setReceipt(null);
    }
    return () => {
      control.serial += 1;
      control.busy = false;
    };
  }, [token, interrupt, setReceipt, invalidate]);
  useAuthorityLock(state, onLockChange);
  const draftActions = authorityDraftActions(state);
  return {
    ...state,
    disclosure,
    ...draftActions,
    edit: () => {
      if (state.busy || state.uncertain || delivery.current.busy) {
        return;
      }
      state.clearPrepared();
      state.setValues({ ...state.values, operationId: crypto.randomUUID() });
      state.setConfirmed(false);
    },
    discard: () => {
      if (!delivery.current.busy && !delivery.current.dispatched) {
        draftActions.discard();
      }
    },
    review: (command = fallback) => {
      reviewAuthority(state, command);
    },
    execute: () => executeAuthority(state, token, delivery.current, disclosure.invalidate),
  };
};
export type AuthorityOperation = ReturnType<typeof useAuthorityOperation>;
