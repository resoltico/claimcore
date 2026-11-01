import { useRequesterContext } from "../hooks/useRequesterContext";
import type { CaseSelector } from "@contracts/client";
import { useEffect, useState, type ComponentProps } from "react";
import { ViewModule } from "./ViewModule";
import type { Notice } from "../protocol/notices";
import type { CurrentCase, CaseReceipt } from "../api/service";
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
import { CaseList } from "./CaseList";
import { MissingPage } from "./MissingPage";
import type { OperationEditor as OperationEditorType } from "./OperationEditor";
import type { RecoveryPanel as RecoveryPanelType } from "./RecoveryPanel";

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
const RecoveryPanel = (props: ComponentProps<typeof RecoveryPanelType>) => (
  <ViewModule load={loadRecoveryPanel} viewProps={props} />
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
/** A return observation belongs to this mounted requester workspace, never history or a journal. */
const useCaseNavigation = () => {
  const verified = useRequesterContext() !== null;
  const [returnTo, setReturnTo] = useState<{
    url: string;
    scrollY: number;
    focusId: string;
  } | null>(null);
  const remember = (focusId: string) => {
    setReturnTo({ url: location.pathname + location.search, scrollY: window.scrollY, focusId });
  };
  return {
    selectCase: (target: CaseSelector) => {
      remember(target.kind === "ID" ? `case-${target.caseId}` : "case-lookup");
      navigate(
        target.kind === "ID" ? casePath(target.caseId) : referencePath(target.caseReference),
      );
    },
    openCase: () => {
      remember("open-case");
      navigate("/cases/new");
    },
    backToCases: () => {
      const owned = verified ? returnTo : null;
      navigate(owned?.url ?? "/cases", false, owned?.focusId, owned?.scrollY);
    },
  };
};
const selectAct = (current: CurrentCase, act: string): void => {
  navigate(actPath({ kind: "ID", caseId: current.caseId }, act));
};
export type EditorCallbacks = {
  setLocked: (value: boolean) => void;
  committed: (receipt: CaseReceipt) => void;
};
const EditorBootstrap = ({
  route,
  onReady,
}: EditorCallbacks & {
  route: Extract<Route, { screen: "caseAct" }>;
  onReady: (current: CurrentCase) => void;
}) => {
  const p = usePresentation();
  const current = useCaseDisclosure(route.target);
  useEffect(() => {
    if (
      current.value !== null &&
      isCurrentCase(current.value) &&
      isCaseAct(route.act) &&
      current.value.advice.acts.includes(route.act)
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
    value === null ||
    !isCurrentCase(value) ||
    !isCaseAct(route.act) ||
    !value.advice.acts.some((act) => act === route.act)
  ) {
    return (
      <section>
        <h1>{p.text("ui.caseDetail")}</h1>
        <button
          type="button"
          onClick={() => {
            navigate(
              route.target.kind === "ID"
                ? casePath(route.target.caseId)
                : referencePath(route.target.caseReference),
            );
          }}
        >
          {p.text("ui.backToCase")}
        </button>
      </section>
    );
  }
  return <p role="status">{p.text("ui.loadingCurrentCase")}</p>;
};
const RoutedEditor = (
  props: EditorCallbacks & {
    route: Extract<Route, { screen: "caseAct" }>;
    disclosureAvailable: boolean;
  },
) => {
  const [opened, setOpened] = useState<CurrentCase | null>(null);
  if (!isCaseAct(props.route.act)) {
    return <MissingPage />;
  }
  return opened === null ? (
    <EditorBootstrap {...props} onReady={setOpened} />
  ) : (
    <OperationEditor
      definition={productMetadata}
      current={opened}
      initialAct={props.route.act}
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
export const RoutedContent = ({
  route,
  setLocked,
  committed,
  refresh,
  authorityAvailable = true,
  caseAvailable = true,
}: EditorCallbacks & {
  route: Route;
  refresh: number;
  authorityAvailable?: boolean;
  caseAvailable?: boolean;
}) => {
  const { selectCase, openCase, backToCases } = useCaseNavigation();
  switch (route.screen) {
    case "cases":
      return <CaseList onSelect={selectCase} onOpen={openCase} />;
    case "case":
      return (
        <CaseDetail
          target={route.target}
          reloadSignal={refresh}
          definition={productMetadata.catalog}
          onBack={backToCases}
          onAct={selectAct}
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
          setLocked={setLocked}
          committed={committed}
        />
      );
    case "operations":
      return (
        <RecoveryPanel
          key={route.operationId}
          {...(route.operationId === "" ? {} : { initialOperationId: route.operationId })}
        />
      );
    case "authority":
      return <AuthorityPanel onLockChange={setLocked} disclosureAvailable={authorityAvailable} />;
    case "missing":
      return <MissingPage />;
  }
};
