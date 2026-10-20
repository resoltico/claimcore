import { usePresentation } from "../presentation/context";
import { Button } from "react-aria-components/Button";
import { useState, useCallback } from "react";
import type { CurrentCase, ProductMetadata, CaseReceipt } from "../api/service";
import type { CommandKind } from "../interaction/metadata";
import { service, type ServiceResponse } from "../api/service";
import { useRead } from "../hooks/useRead";
import { NoticeView } from "../presentation/Message";
import { productMetadata } from "../protocol/productMetadata";
import { AuthorityPanel } from "./AuthorityPanel";
import { CaseDetail } from "./CaseDetail";
import { CaseList } from "./CaseList";
import { OperationEditor } from "./OperationEditor";
import { OperationLookup } from "./OperationLookup";

type OperationTarget = { current: CurrentCase | null; command: CommandKind };
type DashboardProps = { token: string; onLogout: () => Promise<void> };

const Header = ({
  definition,
  locked,
  onLogout,
}: {
  definition: ProductMetadata;
  locked: boolean;
  onLogout: () => Promise<void>;
}) => {
  const p = usePresentation();
  return (
    <header>
      <div>
        <p className="eyebrow">{p.text("ui.registerLabel")}</p>
        <h1>ClaimCore</h1>
        <details>
          <summary>{p.text("ui.technicalDetails")}</summary>
          <p>{p.text("ui.productVersion", { version: definition.productVersion })}</p>
          <p>{p.text("ui.webIdentity", { digest: definition.contractIdentity })}</p>
        </details>
      </div>
      <Button onPress={() => void onLogout()} isDisabled={locked}>
        {p.text("ui.signOut")}
      </Button>
    </header>
  );
};

const DashboardNav = ({
  active,
  locked,
  navigate,
  views,
}: {
  active: string;
  views: ReadonlyArray<string>;
  locked: boolean;
  navigate: (next: "cases" | "operations" | "authority") => void;
}) => {
  const p = usePresentation();
  return (
    <nav aria-label={p.text("ui.primary")}>
      {(["cases", "operations", "authority"] as const)
        .filter((item) => views.includes(item.toUpperCase()))
        .map((item) => (
          <Button
            key={item}
            onPress={() => {
              navigate(item);
            }}
            isDisabled={locked}
            aria-pressed={active === item}
          >
            {p.text(`ui.${item}`)}
          </Button>
        ))}
    </nav>
  );
};

type ContentProps = {
  token: string;
  definition: ProductMetadata;
  active: string;
  operation: OperationTarget | null;
  selectedReference: string | null;
  refresh: number;
  setOperation: (value: OperationTarget | null) => void;
  setSelectedReference: (value: string | null) => void;
  setLocked: (value: boolean) => void;
  committed: (receipt: CaseReceipt) => void;
};

type PaneProps = Pick<
  ContentProps,
  "token" | "selectedReference" | "refresh" | "setOperation" | "setSelectedReference"
> & { definition: NonNullable<ContentProps["definition"]> };

const CasesPane = ({
  token,
  definition,
  selectedReference,
  refresh,
  setOperation,
  setSelectedReference,
}: PaneProps) => {
  if (selectedReference === null) {
    return (
      <CaseList
        token={token}
        onSelect={setSelectedReference}
        onOpen={() => {
          setOperation({ current: null, command: "OPEN" });
        }}
      />
    );
  }
  return (
    <CaseDetail
      token={token}
      caseReference={selectedReference}
      reloadSignal={refresh}
      definition={definition.descriptors}
      onBack={() => {
        setSelectedReference(null);
      }}
      onCommand={(current, command) => {
        setOperation({ current, command });
      }}
    />
  );
};

const DashboardContent = (props: ContentProps) => {
  const { definition, operation } = props;
  const workflow = {
    token: props.token,
    onMutationLockChange: props.setLocked,
  };
  if (operation !== null) {
    return (
      <OperationEditor
        {...workflow}
        definition={definition}
        current={operation.current}
        initialCommand={operation.command}
        onClose={() => {
          props.setOperation(null);
        }}
        onCommitted={props.committed}
      />
    );
  }
  if (props.active === "authority") {
    return <AuthorityPanel token={props.token} onLockChange={props.setLocked} />;
  }
  if (props.active === "operations") {
    return <OperationLookup token={props.token} definition={definition} />;
  }
  return (
    <CasesPane
      token={props.token}
      definition={definition}
      selectedReference={props.selectedReference}
      refresh={props.refresh}
      setOperation={props.setOperation}
      setSelectedReference={props.setSelectedReference}
    />
  );
};

const useDashboardNavigation = () => {
  const [active, setActive] = useState<"cases" | "operations" | "authority">("cases");
  const [selectedReference, setSelectedReference] = useState<string | null>(null);
  const [operation, setOperation] = useState<OperationTarget | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [locked, setLocked] = useState(false);
  const navigate = (next: "cases" | "operations" | "authority"): void => {
    if (!locked && operation === null) {
      setActive(next);
      setSelectedReference(null);
    }
  };
  const committed = (receipt: CaseReceipt): void => {
    setOperation(null);
    setSelectedReference(receipt.caseReference);
    setRefresh((value) => value + 1);
  };
  return {
    active,
    selectedReference,
    operation,
    refresh,
    locked,
    setOperation,
    setSelectedReference,
    setLocked,
    navigate,
    committed,
  };
};

const contextData = (reply: ServiceResponse<"context">) =>
  reply.outcome.tag === "SUCCEEDED" ? reply.outcome.data : null;

export const Dashboard = ({ token, onLogout }: DashboardProps) => {
  const navigation = useDashboardNavigation();
  const definition = productMetadata;
  const requestContext = useCallback(
    (signal: AbortSignal) => service.context(token, signal),
    [token],
  );
  const context = useRead(requestContext, contextData, token);
  const views: ReadonlyArray<string> = context.value?.views ?? [];
  const active = views.includes(navigation.active.toUpperCase())
    ? navigation.active
    : (["cases", "authority", "operations"] as const).find((item) =>
        views.includes(item.toUpperCase()),
      );
  const locked = navigation.locked || navigation.operation !== null;

  return (
    <main className="app-shell">
      <Header definition={definition} locked={locked} onLogout={onLogout} />
      <DashboardNav
        active={active ?? ""}
        locked={locked}
        navigate={navigation.navigate}
        views={views}
      />
      {context.message === null ? null : <NoticeView value={context.message} />}
      {active === undefined ? null : (
        <DashboardContent {...navigation} active={active} token={token} definition={definition} />
      )}
    </main>
  );
};
