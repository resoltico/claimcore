import { isDisclosureRefused } from "../../api/outcomes";
import { prepareRequest, ProtocolInputError } from "../../protocol/preparedRequest";
import { resolutionEvidence } from "../../protocol/delivery";
import { isMutationUncertain, resultNotice, service } from "../../api/service";
import { createDraft } from "../../interaction/metadata";
import type {
  EditorState,
  OperationEditorProps,
  SubmissionRequest,
} from "../../interaction/editorTypes";
import { acceptedReceipt } from "../../interaction/editorSupport";
const preparedForPreview = (props: OperationEditorProps, state: EditorState) =>
  state.state.prepared ??
  prepareRequest(
    "case.execute",
    state.state.exposedRequest ??
      createDraft(
        state.state.operationId,
        state.state.caseReference,
        props.current?.revision ?? 0,
        state.state.command,
        state.state.values,
      ),
  );
export const sendPreview = async (
  props: OperationEditorProps,
  state: EditorState,
  requestId: number,
): Promise<void> => {
  let prepared;
  try {
    prepared = preparedForPreview(props, state);
  } catch (error) {
    if (!(error instanceof ProtocolInputError)) {
      throw error;
    }
    state.dispatch({
      type: "INPUT_INVALID",
      field: error.path.at(-1) ?? "",
      message: { kind: "local", reason: "invalidInput" },
    });
    return;
  }
  state.dispatch({ type: "PREVIEWING", requestId, draft: prepared.input, prepared });
  const result = await service.preview(prepared, props.token);
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
  state.dispatch({
    type: "DEFINITELY_REJECTED",
    requestId,
    message: resultNotice(result),
    disclosureRefused: isDisclosureRefused(result),
    field,
    contentExcluded: resolutionEvidence(result) === "CONTENT_EXCLUDED",
  });
};
export const sendSubmit = async ({
  prepared,
  token,
  dispatch,
  requestId,
}: SubmissionRequest & { requestId: number }): Promise<void> => {
  if (prepared === null) {
    return;
  }
  dispatch({ type: "SUBMITTING", requestId });
  const result = await service.execute(prepared, token);
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
  dispatch({
    type: "DEFINITELY_REJECTED",
    requestId,
    message: resultNotice(result),
    disclosureRefused: isDisclosureRefused(result),
    field,
    contentExcluded: resolutionEvidence(result) === "CONTENT_EXCLUDED",
  });
};
