import { usePresentation } from "../presentation/context";
import { Button } from "react-aria-components/Button";
import { useState } from "react";
import type { CurrentCase, PublicDefinition, Receipt } from "../api/service";
import type { CommandKind } from "../domain/metadata";
import { productMetadata } from "../generated/contracts/product-metadata";
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
  definition: PublicDefinition;
  locked: boolean;
  onLogout: () => Promise<void>;
}) => {
  const p = usePresentation();
  return (
    <header>
      <div>
        <p className="eyebrow">{p.text("ui.registerLabel")}</p>
        <h1>{definition.definition.application}</h1>
        <details>
          <summary>{p.text("ui.technicalDetails")}</summary>
          <p>{p.text("ui.productVersion", { version: definition.productVersion })}</p>
          <p>{p.text("ui.semanticIdentity", { digest: definition.semanticFingerprint })}</p>
          <p>{p.text("ui.webIdentity", { digest: definition.wireFingerprint })}</p>
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
}: {
  active: string;
  locked: boolean;
  navigate: (next: "cases" | "operations") => void;
}) => {
  const p = usePresentation();
  return (
    <nav aria-label={p.text("ui.primary")}>
      {(["cases", "operations"] as const).map((item) => (
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
  definition: PublicDefinition;
  active: string;
  operation: OperationTarget | null;
  selectedReference: string | null;
  refresh: number;
  setOperation: (value: OperationTarget | null) => void;
  setSelectedReference: (value: string | null) => void;
  setLocked: (value: boolean) => void;
  committed: (receipt: Receipt) => void;
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
  const [active, setActive] = useState<"cases" | "operations">("cases");
  const [selectedReference, setSelectedReference] = useState<string | null>(null);
  const [operation, setOperation] = useState<OperationTarget | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [locked, setLocked] = useState(false);
  const navigate = (next: "cases" | "operations"): void => {
    if (!locked && operation === null) {
      setActive(next);
      setSelectedReference(null);
    }
  };
  const committed = (receipt: Receipt): void => {
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

export const Dashboard = ({ token, onLogout }: DashboardProps) => {
  const navigation = useDashboardNavigation();
  const definition = productMetadata;
  const locked = navigation.locked || navigation.operation !== null;

  return (
    <main className="app-shell">
      <Header definition={definition} locked={locked} onLogout={onLogout} />
      <DashboardNav active={navigation.active} locked={locked} navigate={navigation.navigate} />
      <DashboardContent {...navigation} token={token} definition={definition} />
    </main>
  );
};
