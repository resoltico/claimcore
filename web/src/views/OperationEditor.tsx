import { ScreenView } from "../ui/ScreenView";
import { captureAmountEntry, normalizeAmountValues } from "../presentation/amountEntry";
import { useCallback, useEffect, useEffectEvent, useLayoutEffect, useRef } from "react";
import { useOperationNavigation } from "../hooks/useOperationNavigation";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { useCredential } from "../session/SessionContext";
import { usePresentation } from "../presentation/context";
import { useOperation } from "../hooks/operation/useOperation";
import { AcceptedOperation } from "./operation/OperationDialogs";
import { OperationPanel, OperationDiscard } from "./operation/OperationPanel";
import type { OperationEditorProps } from "../operation/editorTypes";
const useCaseOperation = (props: OperationEditorProps) => {
  const credential = useCredential();
  const p = usePresentation();
  const model = useOperation({
    subject: "CASE",
    kind: props.initialAct,
    current: props.current,
    credential,
    normalizeValues: normalizeAmountValues,
    captureValue: (path, value, entry) => captureAmountEntry(path, value, entry, p.displayLocale),
    onLockChange: props.onMutationLockChange,
  });
  const invalidate = useEffectEvent(() => {
    model.invalidateDisclosure();
  });
  useLayoutEffect(() => {
    if (props.disclosureAvailable === false) {
      invalidate();
    }
  }, [props.disclosureAvailable]);
  const navigation = useOperationNavigation(model, props.onClose);
  const recorded = useRef<string | null>(null);
  const commit = useCallback(
    (receipt: Parameters<OperationEditorProps["onCommitted"]>[0]) => {
      if (recorded.current !== receipt.operationId) {
        recorded.current = receipt.operationId;
        navigation.committed();
        props.onCommitted(receipt);
      }
    },
    [navigation, props],
  );
  useEffect(() => {
    if (model.receipt?.subject.kind === "CASE") {
      commit({ ...model.receipt, subject: model.receipt.subject });
    }
  }, [model.receipt, commit]);
  return { model, navigation, commit };
};
const CurrentObservation = ({
  current,
}: {
  current: NonNullable<OperationEditorProps["current"]>;
}) => (
  <section className="case-detail">
    <h1>
      <bdi>{current.caseReference}</bdi>
    </h1>
    <ScreenView view={current.view} timeZone={current.work.basis.timeZone} />
  </section>
);
export const OperationEditor = (props: OperationEditorProps) => {
  const { model, navigation, commit } = useCaseOperation(props);
  const requester = useRequesterContext();
  if (props.disclosureAvailable !== false && model.receipt?.subject.kind === "CASE") {
    return (
      <AcceptedOperation
        definition={props.definition}
        receipt={{ ...model.receipt, subject: model.receipt.subject }}
        message={model.notice}
        onCommitted={commit}
      />
    );
  }
  const current = model.currentCase;
  const offered =
    current?.advice.acts ??
    (props.current === null &&
    props.initialAct === "OPEN" &&
    requester?.offers.some((offer) => offer.kind === "OPEN") === true
      ? ["OPEN"]
      : []);
  return (
    <>
      {current === null || props.disclosureAvailable === false ? null : (
        <CurrentObservation current={current} />
      )}
      <OperationDiscard model={model} onClose={navigation.close} />
      <OperationPanel
        model={model}
        current={current}
        offered={offered}
        disclosureAvailable={props.disclosureAvailable !== false}
        onClose={navigation.close}
      />
    </>
  );
};
