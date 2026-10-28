import type { Notice } from "../protocol/notices";
import type { CurrentCase, CaseReceipt } from "../api/service";
import { isCurrentCase } from "../api/service";
import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { productMetadata } from "../protocol/productMetadata";
import { isCaseAct } from "../operation/metadata";
import { actPath, casePath, type Route } from "../routing/routes";
import { navigate } from "../routing/navigation";
import { AuthorityPanel } from "./AuthorityPanel";
import { CaseDetail } from "./CaseDetail";
import { CaseList } from "./CaseList";
import { OperationEditor } from "./OperationEditor";
import { OperationLookup } from "./OperationLookup";

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
const selectCase = (reference: string): void => {
  navigate(casePath(reference));
};
const openCase = (): void => {
  navigate("/cases/new");
};
const backToCases = (): void => {
  navigate("/cases");
};
const selectAct = (current: CurrentCase, act: string): void => {
  navigate(actPath(current.caseReference, act));
};
export type EditorCallbacks = {
  setLocked: (value: boolean) => void;
  committed: (receipt: CaseReceipt) => void;
};
const RoutedEditor = ({
  route,
  setLocked,
  committed,
}: EditorCallbacks & { route: Extract<Route, { screen: "caseAct" }> }) => {
  const p = usePresentation();
  const current = useCaseDisclosure(route.reference);
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
            navigate(casePath(route.reference));
          }}
        >
          {p.text("ui.backToCase")}
        </button>
      </section>
    );
  }
  return (
    <OperationEditor
      definition={productMetadata}
      current={value}
      initialAct={route.act}
      onClose={() => {
        navigate(casePath(route.reference));
      }}
      onCommitted={committed}
      onMutationLockChange={setLocked}
    />
  );
};

const NewCaseEditor = ({ setLocked, committed }: EditorCallbacks) => (
  <OperationEditor
    definition={productMetadata}
    current={null}
    initialAct="OPEN"
    onClose={backToCases}
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
}: EditorCallbacks & { route: Route; refresh: number; authorityAvailable?: boolean }) => {
  switch (route.screen) {
    case "cases":
      return <CaseList onSelect={selectCase} onOpen={openCase} />;
    case "case":
      return (
        <CaseDetail
          caseReference={route.reference}
          reloadSignal={refresh}
          definition={productMetadata.catalog}
          onBack={backToCases}
          onAct={selectAct}
        />
      );
    case "newCase":
      return <NewCaseEditor setLocked={setLocked} committed={committed} />;
    case "caseAct":
      return (
        <RoutedEditor
          key={`${route.reference}:${route.act}`}
          route={route}
          setLocked={setLocked}
          committed={committed}
        />
      );
    case "operations":
      return (
        <OperationLookup
          key={route.operationId}
          initialOperationId={route.operationId}
          definition={productMetadata}
          onLockChange={setLocked}
        />
      );
    case "authority":
      return <AuthorityPanel onLockChange={setLocked} disclosureAvailable={authorityAvailable} />;
    case "missing":
      return <CaseList onSelect={selectCase} onOpen={openCase} />;
  }
};
