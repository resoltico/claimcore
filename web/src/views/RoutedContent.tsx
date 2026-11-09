import type { AuthorityHistory as AuthorityHistoryType } from "./authority/AuthorityHistory";
import { SettingsPage } from "../features/settings/SettingsPage";
import type { useCaseNavigation } from "../routing/useCaseNavigation";
import { useEffect, useState, type ComponentProps } from "react";
import { ViewModule } from "./ViewModule";
import type { Notice } from "../protocol/notices";
import type { CurrentCase, CaseOperationReceipt } from "../api/service";
import { isCurrentCase } from "../api/service";
import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { productMetadata } from "../protocol/productMetadata";
import { isCaseAct } from "../operation/metadata";
import { actPath, casePath, referencePath, type Route } from "../routing/routes";
import { navigate } from "../routing/navigation";
import type { AuthorityPanel as AuthorityPanelType } from "./AuthorityPanel";
import type { CaseDetail as CaseDetailType } from "./CaseDetail";
import { ReminderQueue } from "./work/ReminderQueue";
import { CaseList } from "./CaseList";
import { MissingPage } from "./MissingPage";
import type { OperationEditor as OperationEditorType } from "./OperationEditor";

const loadAuthorityHistory = () =>
  import("./authority/AuthorityHistory").then((module) => module.AuthorityHistory);
const AuthorityHistory = (props: ComponentProps<typeof AuthorityHistoryType>) => (
  <ViewModule load={loadAuthorityHistory} viewProps={props} />
);
const loadAuthorityPanel = () => import("./AuthorityPanel").then((module) => module.AuthorityPanel);
const AuthorityPanel = (props: ComponentProps<typeof AuthorityPanelType>) => (
  <ViewModule load={loadAuthorityPanel} viewProps={props} />
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
      <h1>{p.text("ui.caseDetail")}</h1>
      <p role="alert">
        <NoticeView value={message} />
      </p>
      <button type="button" onClick={retry}>
        {p.text("ui.tryAgain")}
      </button>
    </section>
  );
};
const selectAct = (current: CurrentCase, act: string, paymentId?: string): void => {
  const path = actPath({ kind: "ID", caseId: current.context.caseId }, act);
  navigate(paymentId === undefined ? path : `${path}?${new URLSearchParams({ paymentId })}`);
};
export type EditorCallbacks = {
  setLocked: (value: boolean) => void;
  committed: (receipt: CaseOperationReceipt) => void;
};
const EditorBootstrap = ({
  route,
  onReady,
  onBack,
}: EditorCallbacks & {
  route: Extract<Route, { screen: "caseAct" }>;
  onReady: (current: CurrentCase) => void;
  onBack: () => void;
}) => {
  const p = usePresentation();
  const current = useCaseDisclosure(route.target);
  useEffect(() => {
    if (
      current.value !== null &&
      isCurrentCase(current.value) &&
      isCaseAct(route.act) &&
      current.value.context.tasks.includes(`CASE:${route.act}`)
    ) {
      onReady(current.value);
    }
  }, [current.value, onReady, route.act]);
  if (current.loading) {
    return <p role="status">{p.text("ui.loadingCurrentCase")}</p>;
  }
  if (current.message !== null) {
    return <EditorFailure message={current.message} retry={current.revalidate} />;
  }
  const { value } = current;
  if (
    value !== null &&
    isCaseAct(route.act) &&
    !value.context.tasks.includes(`CASE:${route.act}`)
  ) {
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
  const [opened, setOpened] = useState<CurrentCase | null>(null);
  if (
    !isCaseAct(props.route.act) ||
    (props.route.act === "REMOVE_PAYMENT" && props.route.paymentId === undefined)
  ) {
    return <MissingPage />;
  }
  return opened === null ? (
    <EditorBootstrap {...props} onReady={setOpened} />
  ) : (
    <OperationEditor
      definition={productMetadata}
      current={opened}
      initialAct={props.route.act}
      binding={{
        kind: "CASE",
        target: props.route.target,
        revision: opened.context.revision,
        ...(props.route.paymentId === undefined ? {} : { paymentId: props.route.paymentId }),
      }}
      {...(props.route.act === "REMOVE_PAYMENT" && props.route.paymentId !== undefined
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
}: EditorCallbacks & { disclosureAvailable: boolean; onClose: () => void }) => (
  <OperationEditor
    definition={productMetadata}
    current={null}
    initialAct="OPEN"
    disclosureAvailable={disclosureAvailable}
    onClose={onClose}
    onCommitted={committed}
    onMutationLockChange={setLocked}
  />
);
const AreaContent = ({
  route,
  setLocked,
  caseAvailable,
}: {
  route: Route;
  setLocked: (locked: boolean) => void;
  caseAvailable: boolean;
}) => {
  switch (route.screen) {
    case "settings":
      return <SettingsPage />;
    case "reminders":
      return <ReminderQueue onLockChange={setLocked} disclosureAvailable={caseAvailable} />;
    case "recovery":
      return <RecoveryPanel key={route.operationId} operationId={route.operationId} />;
    case "authorityHistory":
      return <AuthorityHistory key={route.actorId} actorId={route.actorId} />;
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
  authorityAvailable = true,
  caseAvailable = true,
  caseNavigation,
}: EditorCallbacks & {
  route: Route;
  refresh: number;
  authorityAvailable?: boolean;
  caseAvailable?: boolean;
  caseNavigation: ReturnType<typeof useCaseNavigation>;
}) => {
  const { selectCase, backToCases } = caseNavigation;
  switch (route.screen) {
    case "cases":
      return (
        <CaseList
          onSelect={selectCase}
          onMutationLockChange={setLocked}
          disclosureAvailable={caseAvailable}
        />
      );
    case "case":
      return (
        <CaseDetail
          target={route.target}
          reloadSignal={refresh}
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
      return <AuthorityPanel onLockChange={setLocked} disclosureAvailable={authorityAvailable} />;
    default:
      return <AreaContent route={route} setLocked={setLocked} caseAvailable={caseAvailable} />;
  }
};
