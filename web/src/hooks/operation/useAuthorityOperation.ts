import {
  prepareRequest,
  ProtocolInputError,
  type PreparedRequest,
} from "../../protocol/preparedRequest";
import { useEffect, useState, useRef } from "react";
import { buildRequest, wireSchemas } from "@contracts/browser-contract";
import { service, resultNotice, isMutationUncertain } from "../../api/service";
import { localNotice, type Notice } from "../../api/notices";
import { formValue, type FormValues } from "../../protocol/forms";
import { resolutionEvidence } from "../../protocol/delivery";
export const authorityDocument = wireSchemas["authority.execute"].request;
const useAuthorityState = () => {
  const [values, setValues] = useState<FormValues>({ operationId: crypto.randomUUID() });
  const [prepared, setPrepared] = useState<PreparedRequest<"authority.execute"> | null>(null);
  const bytes = prepared?.bytes ?? null;
  const setBytes = (_value: null) => {
    setPrepared(null);
  };
  const [fieldError, setFieldError] = useState<string | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [uncertain, setUncertain] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [pendingDiscard, setPendingDiscard] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<string | null>(null);
  return {
    prepared,
    setPrepared,
    fieldError,
    setFieldError,
    pendingDiscard,
    setPendingDiscard,
    values,
    setValues,
    bytes,
    setBytes,
    confirmed,
    setConfirmed,
    busy,
    setBusy,
    uncertain,
    setUncertain,
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
const executeAuthority = async (state: State, token: string, control: DeliveryControl) => {
  if (state.prepared === null || control.busy || (!state.confirmed && !state.uncertain)) {
    return;
  }
  control.busy = true;
  control.dispatched = true;
  const serial = ++control.serial;
  state.setBusy(true);
  const result = await service.authorityExecute(state.prepared, token);
  if (control.serial !== serial) {
    return;
  }
  control.busy = false;
  control.dispatched = false;
  state.setBusy(false);
  if (
    result.kind === "outcome" &&
    (result.value.outcome.tag === "ACCEPTED" || result.value.outcome.tag === "ALREADY_ACCEPTED")
  ) {
    state.setValues({ operationId: crypto.randomUUID() });
    state.setReceipt(result.value.outcome.data.operationId);
    state.setBytes(null);
    state.setUncertain(false);
    return;
  }
  state.setNotice(resultNotice(result));
  state.setUncertain(
    resolutionEvidence(result) !== "CONTENT_EXCLUDED" &&
      (state.uncertain || isMutationUncertain(result)),
  );
};
const authorityDraftActions = (state: State) => {
  const discard = () => {
    state.setValues({
      operationId: crypto.randomUUID(),
      ...(state.pendingDiscard !== null && state.pendingDiscard !== ""
        ? { "act.kind": state.pendingDiscard }
        : {}),
    });
    state.setBytes(null);
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
export const useAuthorityOperation = (
  token: string,
  fallback: string,
  onLockChange: (locked: boolean) => void,
) => {
  const state = useAuthorityState();
  const delivery = useRef<DeliveryControl>({ busy: false, serial: 0, dispatched: false, token });
  const { setBusy, setUncertain, setReceipt } = state;
  useEffect(() => {
    const control = delivery.current;
    if (control.token !== token) {
      control.token = token;
      setBusy(false);
      setUncertain((prior) => control.dispatched || prior);
      setReceipt(null);
    }
    return () => {
      control.serial += 1;
      control.busy = false;
    };
  }, [token, setBusy, setUncertain, setReceipt]);
  useEffect(() => {
    onLockChange(state.bytes !== null || state.busy || Object.keys(state.values).length > 1);
    return () => {
      onLockChange(false);
    };
  }, [state.bytes, state.busy, state.values, onLockChange]);
  return {
    ...state,
    ...authorityDraftActions(state),
    review: () => {
      reviewAuthority(state, fallback);
    },
    execute: () => executeAuthority(state, token, delivery.current),
  };
};
export type AuthorityOperation = ReturnType<typeof useAuthorityOperation>;
