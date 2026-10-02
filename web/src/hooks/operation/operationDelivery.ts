import { recoveryNotice } from "../../api/notices";
import { isMutationUncertain, resultNotice, v3 } from "../../api/v3";
import { createDraft } from "../../domain/metadata";
import type {
  EditorState,
  OperationEditorProps,
  SubmissionRequest,
} from "../../views/operation/editorTypes";
import { acceptedReceipt, prepared } from "../../views/operation/editorSupport";

const draftForPrepare = (props: OperationEditorProps, state: EditorState) =>
  state.state.exposedRequest ??
  createDraft(
    state.state.operationId,
    state.state.caseReference,
    props.current?.case.revision ?? "0",
    state.state.command,
    { ...state.state.values },
  );

const dispatchPrepareResult = (
  state: EditorState,
  requestId: number,
  result: Awaited<ReturnType<typeof v3.prepare>>,
): void => {
  const value = result.kind === "outcome" ? prepared(result.value) : null;
  if (value !== null) {
    state.dispatch({
      type: "PREPARED",
      requestId,
      preparation: value.details,
      review: value.review,
    });
    return;
  }
  if (result.kind === "outcome" && result.value.outcome.tag === "OBSERVED_ACCEPTED") {
    state.dispatch({ type: "ACCEPTED", requestId, receipt: result.value.outcome.data.receipt });
    return;
  }
  if (result.kind === "outcome" && result.value.outcome.tag === "RETAINED_FOR_RECOVERY") {
    const { details, rejection } = result.value.outcome.data;
    state.dispatch({
      type: "RETAINED_FOR_RECOVERY",
      requestId,
      preparation: details,
      message: recoveryNotice(
        { kind: "diagnostic", diagnostic: rejection.diagnostic },
        "inspectBeforeAction",
      ),
    });
    return;
  }
  if (isMutationUncertain(result)) {
    state.dispatch({
      type: "PREPARATION_UNKNOWN",
      requestId,
      message: recoveryNotice(resultNotice(result), "inspectBeforeRetry"),
    });
    return;
  }
  const field =
    result.kind === "outcome" && result.value.outcome.tag === "REJECTED"
      ? result.value.outcome.data.rejection.field
      : null;
  state.dispatch({ type: "DEFINITELY_REJECTED", requestId, message: resultNotice(result), field });
};

export const sendPrepare = async (
  props: OperationEditorProps,
  state: EditorState,
  requestId: number,
): Promise<void> => {
  const draft = draftForPrepare(props, state);
  state.dispatch({ type: "PREPARING", requestId, draft });
  const result = await v3.prepare(draft, props.token);
  dispatchPrepareResult(state, requestId, result);
  if (result.kind === "outcome" && result.value.outcome.tag === "RETAINED_FOR_RECOVERY") {
    const { details, rejection } = result.value.outcome.data;
    props.onRecovery({
      operationId: draft.operationId,
      requestSha256: details.summary.requestSha256,
      message: recoveryNotice(
        { kind: "diagnostic", diagnostic: rejection.diagnostic },
        "inspectBeforeAction",
      ),
    });
  }
};

export const sendSubmit = async ({
  preparation,
  draft,
  token,
  dispatch,
  requestId,
  onRecovery,
}: SubmissionRequest & { requestId: number }): Promise<void> => {
  const digest = preparation?.summary.requestSha256;
  if (
    preparation === null ||
    draft === null ||
    draft.operationId !== preparation.summary.operationId ||
    typeof digest !== "string"
  ) {
    return;
  }
  dispatch({ type: "SUBMITTING", requestId });
  const result = await v3.submit(draft, token);
  const receipt = result.kind === "outcome" ? acceptedReceipt(result.value) : null;
  if (receipt !== null) {
    dispatch({
      type: "ACCEPTED",
      requestId,
      receipt,
      message: isMutationUncertain(result)
        ? recoveryNotice(
            { kind: "accepted", operationId: receipt.operationId },
            "inspectBeforeAction",
          )
        : null,
    });
  } else if (isMutationUncertain(result)) {
    const message = recoveryNotice(resultNotice(result), "inspectBeforeAction");
    dispatch({
      type: "OUTCOME_UNKNOWN",
      requestId,
      message,
    });
    onRecovery({ operationId: draft.operationId, requestSha256: digest, message });
  } else {
    dispatch({
      type: "DEFINITELY_REJECTED",
      requestId,
      message: resultNotice(result),
      field: null,
    });
  }
};
