import { isMutationUncertain, resultNotice, service } from "../../api/service";
import { createDraft } from "../../domain/metadata";
import type {
  EditorState,
  OperationEditorProps,
  SubmissionRequest,
} from "../../views/operation/editorTypes";
import { acceptedReceipt } from "../../views/operation/editorSupport";
export const sendPreview = async (
  props: OperationEditorProps,
  state: EditorState,
  requestId: number,
): Promise<void> => {
  const draft =
    state.state.exposedRequest ??
    createDraft(
      state.state.operationId,
      state.state.caseReference,
      props.current?.revision ?? 0,
      state.state.command,
      state.state.values,
    );
  state.dispatch({ type: "PREVIEWING", requestId, draft });
  const result = await service.preview(draft, props.token);
  if (result.kind === "outcome" && result.value.outcome.tag === "REVIEWED") {
    state.dispatch({ type: "REVIEWED", requestId, ...result.value.outcome.data });
    return;
  }
  if (result.kind === "outcome" && result.value.outcome.tag === "ALREADY_ACCEPTED") {
    state.dispatch({ type: "ACCEPTED", requestId, receipt: result.value.outcome.data });
    return;
  }
  const field =
    result.kind === "outcome" && result.value.outcome.tag === "REJECTED"
      ? result.value.outcome.data.field
      : null;
  state.dispatch({ type: "DEFINITELY_REJECTED", requestId, message: resultNotice(result), field });
};
export const sendSubmit = async ({
  bytes,
  token,
  dispatch,
  requestId,
}: SubmissionRequest & { requestId: number }): Promise<void> => {
  if (bytes === null) {
    return;
  }
  dispatch({ type: "SUBMITTING", requestId });
  const result = await service.execute(bytes, token);
  const receipt = result.kind === "outcome" ? acceptedReceipt(result.value) : null;
  if (receipt !== null) {
    dispatch({ type: "ACCEPTED", requestId, receipt });
    return;
  }
  if (isMutationUncertain(result)) {
    dispatch({ type: "OUTCOME_UNKNOWN", requestId, message: resultNotice(result) });
    return;
  }
  const field =
    result.kind === "outcome" && result.value.outcome.tag === "REJECTED"
      ? result.value.outcome.data.field
      : null;
  dispatch({ type: "DEFINITELY_REJECTED", requestId, message: resultNotice(result), field });
};
