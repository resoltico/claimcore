import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { Button } from "react-aria-components/Button";
import { useState } from "react";
import type { CurrentCase, DefinitionPayload } from "../api/v2";
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
        <p className="eyebrow">{p.text("ui.trustedInstallation")}</p>
        <h1>
          {definition === null
            ? "ClaimCore"
            : `${definition.definition.application} ${definition.runtime.productVersion}`}
        </h1>
        {definition === null ? null : (
          <small>
            Semantic {definition.semanticFingerprint} · Web v2 {definition.webFingerprint}
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
          onPress={() => navigate(item)}
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
  committed: () => void;
};

const DashboardContent = ({
  token,
  definition,
  active,
  operation,
  selectedReference,
  refresh,
  setOperation,
  setSelectedReference,
  setLocked,
  committed,
}: ContentProps) => {
  const p = usePresentation();
  if (definition === null) return <p>{p.text("ui.loadingDefinition")}</p>;
  if (operation !== null)
    return (
      <OperationEditor
        token={token}
        definition={definition}
        current={operation.current}
        initialCommand={operation.command}
        onClose={() => setOperation(null)}
        onCommitted={committed}
        onMutationLockChange={setLocked}
      />
    );
  if (active === "recovery") return <RecoveryView token={token} />;
  if (active === "operations") return <OperationLookup token={token} definition={definition} />;
  if (selectedReference === null)
    return (
      <CaseList
        token={token}
        onSelect={setSelectedReference}
        onOpen={() => setOperation({ current: null, command: "OPEN" })}
      />
    );
  return (
    <CaseDetail
      token={token}
      caseReference={selectedReference}
      reloadSignal={refresh}
      definition={definition.definition}
      onBack={() => setSelectedReference(null)}
      onCommand={(current, command) => setOperation({ current, command })}
    />
  );
};

export const Dashboard = ({ token, sessionEpoch, onLogout }: DashboardProps) => {
  const [active, setActive] = useState<"cases" | "recovery" | "operations">("cases");
  const [selectedReference, setSelectedReference] = useState<string | null>(null);
  const [operation, setOperation] = useState<OperationTarget | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [locked, setLocked] = useState(false);
  const { definition, message } = useDefinition(sessionEpoch);
  const navigate = (next: "cases" | "recovery" | "operations"): void => {
    if (!locked) {
      setActive(next);
      setSelectedReference(null);
    }
  };
  const committed = (): void => {
    const reference = operation?.current?.case.fields["caseReference"] ?? null;
    setOperation(null);
    setSelectedReference(reference);
    setRefresh((value) => value + 1);
  };

  return (
    <main className="app-shell">
      <Header definition={definition} locked={locked} onLogout={onLogout} />
      <DashboardNav active={active} locked={locked} navigate={navigate} />
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      <DashboardContent
        {...{
          token,
          definition,
          active,
          operation,
          selectedReference,
          refresh,
          setOperation,
          setSelectedReference,
          setLocked,
          committed,
        }}
      />
    </main>
  );
};
