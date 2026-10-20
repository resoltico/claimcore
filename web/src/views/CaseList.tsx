import { PageStatus } from "../components/PageStatus";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { TextField } from "react-aria-components/TextField";
import { useCallback, useState } from "react";
import type { CaseSummary, ServiceResponse } from "../api/service";
import { service } from "../api/service";
import { CharacterWarning } from "../components/BusinessValue";
import { CopyValue } from "../components/CopyValue";
import { useRetryablePage } from "../hooks/useRead";

type CaseListProps = {
  token: string;
  onSelect: (caseReference: string) => void;
  onOpen: () => void;
};

const casePage = (response: ServiceResponse<"case.list">) =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;

const CaseRow = ({
  caseView,
  onSelect,
}: {
  caseView: CaseSummary;
  onSelect: (reference: string) => void;
}) => {
  const p = usePresentation();
  let status = "ERASED";
  if (caseView.state === "LIVE") {
    status = caseView.disposition === "VOIDED" ? "VOIDED" : caseView.status;
  }
  return (
    <li>
      <Button
        onPress={() => {
          onSelect(caseView.caseReference);
        }}
      >
        <bdi>{caseView.caseReference}</bdi>
      </Button>
      <CharacterWarning value={caseView.caseReference} />
      <span>
        {p.text("ui.caseRow", {
          status: p.token(status),
          revision: p.integer(caseView.revision),
        })}
      </span>
      <CopyValue label={p.fieldLabel("caseReference")} value={caseView.caseReference} />
    </li>
  );
};

const LookupForm = ({
  lookup,
  loading,
  onChange,
  onFind,
  onReload,
}: {
  lookup: string;
  loading: boolean;
  onChange: (value: string) => void;
  onFind: () => void;
  onReload: () => void;
}) => {
  const p = usePresentation();
  return (
    <Form
      onSubmit={(event) => {
        event.preventDefault();
        onFind();
      }}
      className="lookup"
    >
      <TextField value={lookup} onChange={onChange} isRequired>
        <Label>{p.text("ui.exactCaseReference", { label: p.fieldLabel("caseReference") })}</Label>
        <Input id="case-lookup" autoComplete="off" dir="auto" />
      </TextField>
      <Button type="submit" isDisabled={lookup === ""}>
        {p.text("ui.findCase")}
      </Button>
      <Button onPress={onReload} isDisabled={loading}>
        {loading ? p.text("ui.loading") : p.text("ui.reloadCases")}
      </Button>
    </Form>
  );
};

const CaseHeading = ({ mayOpen, onOpen }: { mayOpen: boolean; onOpen: () => void }) => {
  const p = usePresentation();
  return (
    <div className="section-heading">
      <h2 id="case-list-title">{p.text("ui.cases")}</h2>
      {mayOpen ? <Button onPress={onOpen}>{p.commandLabel("OPEN")}</Button> : null}
    </div>
  );
};

const useCaseList = (token: string) => {
  const request = useCallback(
    (cursor: string | null, signal: AbortSignal) => service.list(cursor, 50, token, signal),
    [token],
  );
  const page = useRetryablePage<
    ServiceResponse<"case.list">,
    CaseSummary,
    NonNullable<ReturnType<typeof casePage>>
  >(request, casePage);
  const mayOpen = page.page?.offers.some((offer) => offer.kind === "OPEN") ?? false;
  return { ...page, mayOpen };
};

export const CaseList = ({ token, onSelect, onOpen }: CaseListProps) => {
  const p = usePresentation();
  const [lookup, setLookup] = useState("");
  const { items, cursor, message, loading, load, mayOpen, page } = useCaseList(token);
  return (
    <section aria-labelledby="case-list-title">
      <CaseHeading mayOpen={mayOpen} onOpen={onOpen} />
      <LookupForm
        lookup={lookup}
        loading={loading}
        onChange={setLookup}
        onFind={() => {
          if (lookup !== "") {
            onSelect(lookup);
          }
        }}
        onReload={() => void load(null)}
      />
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {page !== null && !loading && message === null ? (
        <PageStatus
          empty={items.length === 0}
          cursor={cursor}
          emptyMessage="ui.noAccessibleCases"
        />
      ) : null}
      <ul className="case-list">
        {items.map((caseView) => (
          <CaseRow key={caseView.caseReference} caseView={caseView} onSelect={onSelect} />
        ))}
      </ul>
      {loading ? <p role="status">{p.text("ui.loadingCases")}</p> : null}
      {cursor === null ? null : (
        <Button onPress={() => void load(cursor)} isDisabled={loading}>
          {p.text("ui.moreCases")}
        </Button>
      )}
    </section>
  );
};
