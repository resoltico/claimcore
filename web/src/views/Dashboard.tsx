import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { Button } from "react-aria-components/Button";
import { useState } from "react";
import type { CurrentCase, DefinitionPayload, Receipt } from "../api/v3";
import type { RecoveryTarget } from "../domain/operationState";
import type { CommandKind } from "../domain/metadata";
import { useDefinition } from "../hooks/useDefinition";
import { CaseDetail } from "./CaseDetail";
import { CaseList } from "./CaseList";
import { OperationEditor } from "./OperationEditor";
import { OperationLookup } from "./OperationLookup";
import { RecoveryView } from "./RecoveryView";

type OperationTarget = { current: CurrentCase | null; command: CommandKind };
type DashboardProps = { token: string; sessionEpoch: number; onLogout: () => Promise<void> };

const Header = ({
  definition,
  locked,
  onLogout,
}: {
  definition: DefinitionPayload | null;
  locked: boolean;
  onLogout: () => Promise<void>;
}) => {
  const p = usePresentation();
  return (
    <header>
      <div>
        <p className="eyebrow">{p.text("ui.registerLabel")}</p>
        <h1>
          {definition === null
            ? "ClaimCore"
            : `${definition.definition.application} ${definition.runtime.productVersion}`}
        </h1>
        {definition === null ? null : (
          <small>
            Semantic {definition.semanticFingerprint} · Web v3 {definition.webFingerprint}
          </small>
        )}
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
}: {
  active: string;
  locked: boolean;
  navigate: (next: "cases" | "recovery" | "operations") => void;
}) => {
  const p = usePresentation();
  return (
    <nav aria-label={p.text("ui.primary")}>
      {(["cases", "recovery", "operations"] as const).map((item) => (
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
  definition: DefinitionPayload | null;
  active: string;
  operation: OperationTarget | null;
  selectedReference: string | null;
  refresh: number;
  setOperation: (value: OperationTarget | null) => void;
  setSelectedReference: (value: string | null) => void;
  setLocked: (value: boolean) => void;
  committed: (receipt: Receipt) => void;
  recover: (target: RecoveryTarget) => void;
  recoveryTarget: RecoveryTarget | null;
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
      definition={definition.definition}
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
  const p = usePresentation();
  const workflow = {
    token: props.token,
    onRecovery: props.recover,
    onMutationLockChange: props.setLocked,
  };
  if (definition === null) {
    return <p>{p.text("ui.loadingDefinition")}</p>;
  }
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
  if (props.active === "recovery") {
    return <RecoveryView {...workflow} target={props.recoveryTarget} />;
  }
  if (props.active === "operations") {
    return (
      <OperationLookup
        token={props.token}
        definition={definition}
        initialOperationId={props.recoveryTarget?.operationId ?? ""}
      />
    );
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
  const [active, setActive] = useState<"cases" | "recovery" | "operations">("cases");
  const [selectedReference, setSelectedReference] = useState<string | null>(null);
  const [operation, setOperation] = useState<OperationTarget | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [locked, setLocked] = useState(false);
  const [recoveryTarget, setRecoveryTarget] = useState<RecoveryTarget | null>(null);
  const navigate = (next: "cases" | "recovery" | "operations"): void => {
    if (!locked && operation === null) {
      setActive(next);
      setSelectedReference(null);
    }
  };
  const committed = (receipt: Receipt): void => {
    setOperation(null);
    setSelectedReference(receipt.snapshot.fields.caseReference);
    setRefresh((value) => value + 1);
  };
  const recover = (target: RecoveryTarget): void => {
    setOperation(null);
    setSelectedReference(null);
    setRecoveryTarget(target);
    setLocked(false);
    setActive("recovery");
  };

  return {
    active,
    selectedReference,
    operation,
    refresh,
    locked,
    recoveryTarget,
    setOperation,
    setSelectedReference,
    setLocked,
    navigate,
    committed,
    recover,
  };
};

export const Dashboard = ({ token, sessionEpoch, onLogout }: DashboardProps) => {
  const navigation = useDashboardNavigation();
  const { definition, message } = useDefinition(sessionEpoch);
  const locked = navigation.locked || navigation.operation !== null;

  return (
    <main className="app-shell">
      <Header definition={definition} locked={locked} onLogout={onLogout} />
      <DashboardNav active={navigation.active} locked={locked} navigate={navigation.navigate} />
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      <DashboardContent {...navigation} token={token} definition={definition} />
    </main>
  );
};
