import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../api/notices";
import { Button } from "react-aria-components/Button";
import type { CurrentCase, CatalogDocument } from "../api/service";
import { isCurrentCase } from "../api/service";
import { CaseFieldsView } from "../components/CaseFieldsView";
import { ReferenceSummary } from "../components/ReferenceSummary";
import { CopyValue } from "../components/CopyValue";
import { isCaseAct, type ActKind } from "../interaction/metadata";
import { useCaseDisclosure } from "../hooks/useCaseDisclosure";
import { CaseHistory } from "./CaseHistory";
import { LifecycleStatus } from "./LifecycleStatus";

type CaseDetailProps = {
  token: string;
  caseReference: string;
  reloadSignal: number;
  definition: CatalogDocument;
  onBack: () => void;
  onAct: (current: CurrentCase, act: ActKind) => void;
};

const AvailableCommands = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  definition: CatalogDocument;
  onAct: (act: ActKind) => void;
}) => {
  const p = usePresentation();
  return (
    <section aria-labelledby="acts-title">
      <h2 id="acts-title">{p.text("ui.availableCommands")}</h2>
      <div className="actions">
        {current.advice.acts
          .map((offer) => offer.act)
          .filter(isCaseAct)
          .map((act) => (
            <Button
              key={act}
              onPress={() => {
                onAct(act);
              }}
              aria-label={p.text("ui.commandDescription", {
                label: p.actLabel(act),
                meaning: p.actMeaning(act),
              })}
            >
              {p.actLabel(act)}
            </Button>
          ))}
      </div>
    </section>
  );
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
}) => {
  const p = usePresentation();
  return (
    <>
      <CaseFieldsView
        values={current.state.fields}
        revision={current.revision}
        fields={definition.fields}
        context={p.text("ui.currentCase")}
      />
      <AvailableCommands
        current={current}
        definition={definition}
        onAct={(act) => {
          onAct(current, act);
        }}
      />
    </>
  );
};

const CaseHeader = ({
  caseReference,
  onBack,
}: Pick<CaseDetailProps, "caseReference" | "onBack">) => {
  const p = usePresentation();
  const label = p.fieldLabel("caseReference");
  return (
    <>
      <div className="section-heading">
        <h2 id="case-detail-title" tabIndex={-1}>
          {p.text("ui.caseDetail")}
        </h2>
        <Button onPress={onBack}>{p.text("ui.backToCases")}</Button>
      </div>
      <p>
        <ReferenceSummary id="ui.exactReference" values={{ label, reference: caseReference }} />{" "}
        <CopyValue label={label} value={caseReference} />
      </p>
    </>
  );
};

const CaseDisclosure = ({ token, caseReference, definition, onBack, onAct }: CaseDetailProps) => {
  const p = usePresentation();
  const current = useCaseDisclosure(caseReference, token);
  const state = current.value;
  const lookup = state !== null && isCurrentCase(state) ? state : null;
  return (
    <section aria-labelledby="case-detail-title">
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
      {lookup === null ? null : (
        <CurrentPresentation current={lookup} definition={definition} onAct={onAct} />
      )}
      {state === null ? null : <LifecycleStatus value={state} />}
      {state?.state.tag === "ERASED" ? (
        <p role="status">
          {state.caseReference} · {state.revision}
        </p>
      ) : null}
      {current.historyModes.length ? (
        <CaseHistory
          token={token}
          caseReference={caseReference}
          fields={definition.fields}
          modes={current.historyModes}
          onDisclosureRefused={current.revalidate}
        />
      ) : null}
    </section>
  );
};

export const CaseDetail = (props: CaseDetailProps) => (
  <CaseDisclosure
    key={JSON.stringify([props.caseReference, props.token, props.reloadSignal])}
    {...props}
  />
);
