import { isCaseAct } from "../operation/metadata";
import { linkedCaseReceipt } from "../protocol/types";

import { CaseIdentity } from "./case/CaseIdentity";
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
    ...(props.affordance === undefined ? {} : { affordance: props.affordance }),
    kind: props.initialAct,
    current: props.current,
    prefill: {
      ...(props.initialAct === "case.recordPayment" && props.current !== null
        ? { "field:paymentDate": props.current.basis.observation.businessDate }
        : {}),
      ...props.prefill,
    },
    ...(props.binding === undefined ? {} : { binding: props.binding }),
    credential,
    normalizeValues: normalizeAmountValues,
    captureValue: (path, value, entry) => captureAmountEntry(path, value, entry, p.displayLocale),
    onLockChange: props.onMutationLockChange,
  });
  const reviewed = useRef(false);
  useEffect(() => {
    if (
      props.autoReview === true &&
      model.affordance?.blocked === null &&
      !reviewed.current &&
      model.ownerVerified &&
      model.disclosureCurrent
    ) {
      reviewed.current = true;
      void model.preview();
    }
  }, [props.autoReview, model]);
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
      const operationId = model.prepared?.input.operationId;
      if (operationId !== undefined && recorded.current !== operationId) {
        recorded.current = operationId;
        navigation.committed();
        props.onCommitted(receipt, operationId);
      }
    },
    [navigation, props, model.prepared],
  );
  useEffect(() => {
    const receipt = model.receipt === null ? null : linkedCaseReceipt(model.receipt);
    if (model.ownerVerified && receipt !== null) {
      commit(receipt);
    }
  }, [model.receipt, model.ownerVerified, commit]);
  return { model, navigation, commit };
};
const CurrentObservation = ({
  current,
}: {
  current: NonNullable<OperationEditorProps["current"]>;
}) => (
  <section className="case-detail">
    <CaseIdentity
      region={current.screen.regions.find((region) => region.role === "IDENTITY")}
      attention={current.screen.regions.find((region) => region.role === "ATTENTION")}
      requestedReference={undefined}
      timeZone={current.basis.observation.timeZone}
    />
    <ScreenView
      view={current.screen}
      roles={["NOTICE", "PROGRESS", "FACTS", "PAYMENTS", "WORK", "NEXT_ACTIONS", "DETAILS"]}
      timeZone={current.basis.observation.timeZone}
    />
  </section>
);
const caseOffers = (
  _props: OperationEditorProps,
  current: OperationEditorProps["current"],
  requester: ReturnType<typeof useRequesterContext>,
) =>
  [
    ...(_props.affordance === undefined ? [] : [_props.affordance]),
    ...(current?.actions ?? requester?.actions ?? []),
  ]
    .filter((action) => isCaseAct(action.task) && action.blocked === null)
    .map((action) => action.task);
export const OperationEditor = (props: OperationEditorProps) => {
  const { model, navigation, commit } = useCaseOperation(props);
  const requester = useRequesterContext();
  const receipt = model.receipt === null ? null : linkedCaseReceipt(model.receipt);
  if (props.disclosureAvailable !== false && receipt !== null) {
    return (
      <AcceptedOperation
        definition={props.definition}
        receipt={receipt}
        message={model.notice}
        onCommitted={commit}
      />
    );
  }
  const current = model.currentCase;
  const offered = caseOffers(props, current, requester);
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
