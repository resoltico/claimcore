import { useCredential } from "../session/SessionContext";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../protocol/notices";
import { Button } from "react-aria-components/Button";
import type { CurrentCase, CatalogDocument } from "../api/service";
import { isCurrentCase } from "../api/service";
import { CaseFacts } from "./case/CaseFacts";
import { CaseActions } from "./case/CaseActions";
import { CaseProgress } from "./case/CaseProgress";
import { CharacterWarning } from "../components/BusinessValue";
import "./case.css";
import { CopyValue } from "../components/CopyValue";
import { type ActKind } from "../operation/metadata";
import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { CaseHistory } from "./CaseHistory";
import { LifecycleStatus } from "./LifecycleStatus";

type CaseDetailProps = {
  caseReference: string;
  reloadSignal: number;
  definition: CatalogDocument;
  onBack: () => void;
  onAct: (current: CurrentCase, act: ActKind) => void;
};

const CurrentFeedback = ({ message, loading }: { message: Notice | null; loading: boolean }) => {
  const p = usePresentation();
  return (
    <>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {loading ? <p role="status">{p.text("ui.loadingCurrentCase")}</p> : null}
    </>
  );
};

const CurrentPresentation = ({
  current,
  definition,
  onAct,
}: {
  current: CurrentCase;
  definition: CatalogDocument;
  onAct: (current: CurrentCase, act: ActKind) => void;
}) => (
  <>
    <p className="case-claimant">
      <bdi>{current.state.fields.registration.claimantName}</bdi>
      <CharacterWarning value={current.state.fields.registration.claimantName} />
    </p>
    <CaseProgress current={current} definition={definition} />
    <div className="case-workspace">
      <CaseFacts
        values={current.state.fields}
        revision={current.revision}
        fields={definition.fields}
      />
      <CaseActions
        current={current}
        onAct={(act) => {
          onAct(current, act);
        }}
      />
    </div>
  </>
);

const CaseHeader = ({
  caseReference,
  onBack,
}: Pick<CaseDetailProps, "caseReference" | "onBack">) => {
  const p = usePresentation();
  const label = p.fieldLabel("caseReference");
  return (
    <>
      <div className="section-heading">
        <h1 id="case-detail-title" tabIndex={-1} aria-label={p.text("ui.caseDetail")}>
          <bdi dir="ltr">{caseReference}</bdi>
          <CharacterWarning value={caseReference} />
        </h1>
        <Button onPress={onBack}>{p.text("ui.backToCases")}</Button>
      </div>
      <p>
        <CopyValue label={label} value={caseReference} />
      </p>
    </>
  );
};

const CaseDisclosure = ({ caseReference, definition, onBack, onAct }: CaseDetailProps) => {
  const p = usePresentation();
  const current = useCaseDisclosure(caseReference);
  const state = current.value;
  const lookup = state !== null && isCurrentCase(state) ? state : null;
  return (
    <section className="case-detail" aria-labelledby="case-detail-title">
      <CaseHeader caseReference={caseReference} onBack={onBack} />
      <CurrentFeedback message={current.message} loading={current.loading} />
      {current.message === null ? null : (
        <Button
          onPress={() => {
            current.revalidate();
            document.getElementById("case-detail-title")?.focus();
          }}
        >
          {p.text("ui.tryAgain")}
        </Button>
      )}
      {state === null ? null : (
        <div className={`case-lifecycle case-lifecycle-${state.state.kind.toLowerCase()}`}>
          <LifecycleStatus value={state} />
          <span>{p.text("ui.revision", { revision: p.integer(state.revision) })}</span>
        </div>
      )}
      {lookup === null ? null : (
        <CurrentPresentation current={lookup} definition={definition} onAct={onAct} />
      )}
      {state?.state.kind === "ERASED" ? (
        <p role="status">
          <bdi dir="ltr">{state.caseReference}</bdi> ·{" "}
          <bdi dir="ltr">{p.integer(state.revision)}</bdi>
        </p>
      ) : null}
      {current.historyModes.length ? (
        <CaseHistory
          caseReference={caseReference}
          fields={definition.fields}
          modes={current.historyModes}
          onDisclosureRefused={current.revalidate}
        />
      ) : null}
    </section>
  );
};

export const CaseDetail = (props: CaseDetailProps) => {
  const credential = useCredential();
  return (
    <CaseDisclosure
      key={JSON.stringify([props.caseReference, credential, props.reloadSignal])}
      {...props}
    />
  );
};
