import {
  apiEndpoints,
  type ServiceResponse,
  type CurrentCase,
  type CaseLinkedReceipt,
  isCurrentCase,
} from "../api/service";
import { useCaseCreationControl } from "../hooks/useRequesterContext";
import { hasDocumentSubject } from "../protocol/types";
import { useEndpointPage } from "../hooks/useEndpointPage";
import type { Affordance } from "@contracts/client";

import {
  allActions,
  isOperationAffordance,
  type OperationAffordance,
} from "../operation/affordances";
import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import type { PeopleHistory as PeopleHistoryType } from "./people/PeopleHistory";
import { SettingsPage } from "../features/settings/SettingsPage";
import type { useCaseNavigation } from "../routing/useCaseNavigation";
import { useEffect, useState, useCallback, type ComponentProps } from "react";
import { ViewModule } from "./ViewModule";
import type { Notice } from "../protocol/notices";

import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { productMetadata } from "../protocol/productMetadata";
import { isCaseAct, type ActKind } from "../operation/metadata";
import { actPath, casePath, referencePath, type Route } from "../routing/routes";
import { navigate } from "../routing/navigation";
import type { PeoplePanel as PeoplePanelType } from "./PeoplePanel";
import type { CaseDetail as CaseDetailType } from "./CaseDetail";
import { ReminderQueue } from "./work/ReminderQueue";
import { CaseReport } from "./worklist/SummaryBand";
import { CaseList } from "./CaseList";
import { MissingPage } from "./MissingPage";
import type { OperationEditor as OperationEditorType } from "./OperationEditor";

const loadDisclosureTrail = () =>
  import("./DisclosureTrail").then((module) => module.DisclosureTrail);
const DisclosureTrail = () => <ViewModule load={loadDisclosureTrail} viewProps={{}} />;

const loadPeopleHistory = () =>
  import("./people/PeopleHistory").then((module) => module.PeopleHistory);
const PeopleHistory = (props: ComponentProps<typeof PeopleHistoryType>) => (
  <ViewModule load={loadPeopleHistory} viewProps={props} />
);
const loadPeoplePanel = () => import("./PeoplePanel").then((module) => module.PeoplePanel);
const PeoplePanel = (props: ComponentProps<typeof PeoplePanelType>) => (
  <ViewModule load={loadPeoplePanel} viewProps={props} />
);
const loadCaseDetail = () => import("./CaseDetail").then((module) => module.CaseDetail);
const CaseDetail = (props: ComponentProps<typeof CaseDetailType>) => (
  <ViewModule load={loadCaseDetail} viewProps={props} />
);
const loadOperationEditor = () =>
  import("./OperationEditor").then((module) => module.OperationEditor);
const OperationEditor = (props: ComponentProps<typeof OperationEditorType>) => (
  <ViewModule load={loadOperationEditor} viewProps={props} />
);
const loadRecoveryPanel = () => import("./RecoveryPanel").then((module) => module.RecoveryPanel);
const RecoveryPanel = ({ operationId }: { operationId: string }) => (
  <ViewModule
    load={loadRecoveryPanel}
    viewProps={operationId === "" ? {} : { initialOperationId: operationId }}
  />
);

const EditorFailure = ({ message, retry }: { message: Notice; retry: () => void }) => {
  const p = usePresentation();
  return (
    <section>
      <h1>{p.text("label.caseDetail")}</h1>
      <p role="alert">
        <NoticeView value={message} />
      </p>
      <button type="button" onClick={retry}>
        {p.text("ui.tryAgain")}
      </button>
    </section>
  );
};
const selectAct = (current: CurrentCase, action: Affordance): void => {
  navigate(actPath({ kind: "ID", caseId: current.subject.caseId }, action.task, action.binding));
};
export type EditorCallbacks = {
  setLocked: MutationLockChange;
  committed: (receipt: CaseLinkedReceipt, operationId: string) => void;
};
type EditorRoute = Extract<Route, { screen: "caseAct" }>;
type EditorSelectionProps = {
  current: CurrentCase;
  route: EditorRoute;
  onReady: (value: { current: CurrentCase; action: OperationAffordance }) => void;
  onBack: () => void;
};
const matchesEditorAction = (
  action: Affordance,
  route: EditorRoute,
): action is OperationAffordance => {
  if (action.task !== route.act || !isOperationAffordance(action) || action.blocked !== null) {
    return false;
  }
  const binding: Readonly<Record<string, unknown>> = action.binding;
  return Object.entries(route.binding ?? {}).every(
    ([key, value]) =>
      Object.hasOwn(binding, key) && JSON.stringify(binding[key]) === JSON.stringify(value),
  );
};
const UnavailableEditor = ({ route, onBack }: Pick<EditorSelectionProps, "route" | "onBack">) => {
  if (!isCaseAct(route.act)) {
    return null;
  }
  return (
    <CaseDetail
      target={route.target}
      reloadSignal={0}
      definition={productMetadata.catalog}
      onBack={onBack}
      onAct={selectAct}
      unavailableAct={route.act}
    />
  );
};
const paymentSelectionPage = (reply: ServiceResponse<typeof apiEndpoints.casePayments>) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const { document } = reply.outcome;
  if (!hasDocumentSubject(document, "CASE") || document.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    document,
    items: allActions(document),
    nextCursor: document.nextCursor,
    basis: document.basis.observation,
    screen: document.screen,
    disclosure: document.disclosure,
  };
};
const paymentSelectionKey = (page: NonNullable<ReturnType<typeof paymentSelectionPage>>) =>
  JSON.stringify(page.document.subject);
const PaymentEditorSelection = ({
  current,
  route,
  onReady,
  onBack,
  revalidate,
}: EditorSelectionProps & { revalidate: () => void }) => {
  const p = usePresentation();
  const input = useCallback(
    (cursor: string | null) => ({
      target: route.target,
      limit: 50,
      ...(cursor === null ? {} : { cursor }),
    }),
    [route.target],
  );
  const page = useEndpointPage<
    typeof apiEndpoints.casePayments,
    Affordance,
    NonNullable<ReturnType<typeof paymentSelectionPage>>
  >(
    apiEndpoints.casePayments,
    input,
    paymentSelectionPage,
    revalidate,
    undefined,
    paymentSelectionKey,
  );
  const action = page.items.find((candidate): candidate is OperationAffordance =>
    matchesEditorAction(candidate, route),
  );
  useEffect(() => {
    if (page.page !== null && page.page.document.subject.revision !== current.subject.revision) {
      revalidate();
      return;
    }
    if (action !== undefined) {
      onReady({ current, action });
    } else if (!page.loading && page.message === null && page.cursor !== null) {
      void page.load(page.cursor);
    }
  }, [page, action, current, onReady, revalidate]);
  if (page.message !== null) {
    return (
      <EditorFailure
        message={page.message}
        retry={() => {
          void page.load(null);
        }}
      />
    );
  }
  if (!page.loading && page.cursor === null && action === undefined) {
    return <UnavailableEditor route={route} onBack={onBack} />;
  }
  return <p role="status">{p.text("ui.loadingCurrentCase")}</p>;
};
const EditorBootstrap = ({
  route,
  onReady,
  onBack,
}: EditorCallbacks & {
  route: Extract<Route, { screen: "caseAct" }>;
  onReady: (value: { current: CurrentCase; action: OperationAffordance }) => void;
  onBack: () => void;
}) => {
  const p = usePresentation();
  const current = useCaseDisclosure(route.target);
  const candidates = current.value === null ? [] : allActions(current.value);
  const action = candidates.find((candidate): candidate is OperationAffordance =>
    matchesEditorAction(candidate, route),
  );
  useEffect(() => {
    if (current.value !== null && isCurrentCase(current.value) && action !== undefined) {
      onReady({ current: current.value, action });
    }
  }, [current.value, action, onReady]);

  if (current.loading) {
    return <p role="status">{p.text("ui.loadingCurrentCase")}</p>;
  }
  if (current.message !== null) {
    return <EditorFailure message={current.message} retry={current.revalidate} />;
  }
  const { value } = current;
  if (value !== null && action === undefined) {
    if (route.binding !== undefined && "paymentId" in route.binding && isCurrentCase(value)) {
      return (
        <PaymentEditorSelection
          current={value}
          route={route}
          onReady={onReady}
          onBack={onBack}
          revalidate={current.revalidate}
        />
      );
    }
    return <UnavailableEditor route={route} onBack={onBack} />;
  }

  return <p role="status">{p.text("ui.loadingCurrentCase")}</p>;
};
const RoutedEditor = (
  props: EditorCallbacks & {
    route: Extract<Route, { screen: "caseAct" }>;
    disclosureAvailable: boolean;
    onBack: () => void;
  },
) => {
  const [opened, setOpened] = useState<{
    current: CurrentCase;
    action: OperationAffordance;
  } | null>(null);
  if (!isCaseAct(props.route.act)) {
    return <MissingPage />;
  }
  return opened === null ? (
    <EditorBootstrap {...props} onReady={setOpened} />
  ) : (
    <OperationEditor
      definition={productMetadata}
      current={opened.current}
      initialAct={props.route.act}
      affordance={opened.action}
      {...(opened.action.binding.kind === "CASE" ? { binding: opened.action.binding } : {})}
      {...(Object.keys(props.route.binding ?? {}).some((key) => key !== "kind")
        ? {
            autoReview: true,
          }
        : {})}
      disclosureAvailable={props.disclosureAvailable}
      onClose={() => {
        navigate(
          props.route.target.kind === "ID"
            ? casePath(props.route.target.caseId)
            : referencePath(props.route.target.caseReference),
        );
      }}
      onCommitted={props.committed}
      onMutationLockChange={props.setLocked}
    />
  );
};

const NewCaseEditor = ({
  setLocked,
  committed,
  disclosureAvailable,
  onClose,
}: EditorCallbacks & { disclosureAvailable: boolean; onClose: () => void }) => {
  const action = useCaseCreationControl();
  const [initial, setInitial] = useState<ActKind | null>(null);
  if (initial === null && action !== undefined && isCaseAct(action.task)) {
    setInitial(action.task);
  }
  return initial === null ? (
    <MissingPage />
  ) : (
    <OperationEditor
      definition={productMetadata}
      current={null}
      initialAct={initial}
      disclosureAvailable={disclosureAvailable && action?.task === initial}
      onClose={onClose}
      onCommitted={committed}
      onMutationLockChange={setLocked}
    />
  );
};

const AreaContent = ({
  route,
  setLocked,
  caseAvailable,
}: {
  route: Route;
  setLocked: MutationLockChange;
  caseAvailable: boolean;
}) => {
  switch (route.screen) {
    case "settings":
      return <SettingsPage />;
    case "reminders":
      return <ReminderQueue onLockChange={setLocked} disclosureAvailable={caseAvailable} />;
    case "recovery":
      return <RecoveryPanel key={route.operationId} operationId={route.operationId} />;
    case "disclosureTrail":
      return <DisclosureTrail />;
    case "peopleHistory":
      return <PeopleHistory key={route.actorId} actorId={route.actorId} />;
    case "missing":
      return <MissingPage />;
    default:
      return null;
  }
};
export const RoutedContent = ({
  route,
  setLocked,
  committed,
  refresh,
  peopleAvailable = true,
  caseAvailable = true,
  caseAuthoringAvailable = caseAvailable,
  caseNavigation,
  caseFeedback,
}: EditorCallbacks & {
  route: Route;
  refresh: number;
  peopleAvailable?: boolean;
  caseAvailable?: boolean;
  caseAuthoringAvailable?: boolean;
  caseNavigation: ReturnType<typeof useCaseNavigation>;
  caseFeedback?: React.ReactNode;
}) => {
  const { selectCase, backToCases } = caseNavigation;
  switch (route.screen) {
    case "caseReport":
      return <CaseReport query={route.query} available={caseAvailable} />;
    case "cases":
      return (
        <CaseList
          onSelect={selectCase}
          onOpenCase={caseNavigation.openCase}
          onMutationLockChange={setLocked}
          disclosureAvailable={caseAvailable}
        />
      );
    case "case":
      return (
        <CaseDetail
          target={route.target}
          feedback={caseFeedback}
          reloadSignal={refresh}
          disclosureAvailable={caseAvailable}
          authoringAvailable={caseAuthoringAvailable}
          definition={productMetadata.catalog}
          onBack={backToCases}
          onAct={selectAct}
          onMutationLockChange={setLocked}
        />
      );
    case "newCase":
      return (
        <NewCaseEditor
          onClose={backToCases}
          setLocked={setLocked}
          committed={committed}
          disclosureAvailable={caseAvailable}
        />
      );
    case "caseAct":
      return (
        <RoutedEditor
          key={`${JSON.stringify(route.target)}:${route.act}`}
          route={route}
          disclosureAvailable={caseAvailable}
          onBack={backToCases}
          setLocked={setLocked}
          committed={committed}
        />
      );
    case "people":
      return <PeoplePanel onLockChange={setLocked} disclosureAvailable={peopleAvailable} />;
    default:
      return <AreaContent route={route} setLocked={setLocked} caseAvailable={caseAvailable} />;
  }
};
