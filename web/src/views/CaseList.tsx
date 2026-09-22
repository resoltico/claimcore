import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { TextField } from "react-aria-components/TextField";
import { useCallback, useState } from "react";
import type { CaseSummary, WebV2Response } from "../api/v2";
import { v2 } from "../api/v2";
import { CopyValue } from "../components/CopyValue";
import { useRetryablePage } from "../hooks/useV2Read";

type CaseListProps = {
  token: string;
  onSelect: (caseReference: string) => void;
  onOpen: () => void;
};

const casePage = (response: WebV2Response<"case.list">) =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;

const CaseRow = ({
  caseView,
  onSelect,
}: {
  caseView: CaseSummary;
  onSelect: (reference: string) => void;
}) => {
  const p = usePresentation();
  return (
    <li>
      <Button onPress={() => onSelect(caseView.caseReference)}>
        <bdi>{caseView.caseReference}</bdi>
      </Button>
      <span>
        {p.text("ui.caseRow", {
          status: p.token(caseView.status),
          revision: p.integer(caseView.revision),
        })}
      </span>
      <CopyValue label={p.text("ui.caseReference")} value={caseView.caseReference} />
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
      <TextField value={lookup} onChange={onChange}>
        <Label>{p.text("ui.exactCaseReference")}</Label>
        <Input id="case-lookup" autoComplete="off" dir="auto" />
      </TextField>
      <Button type="submit">{p.text("ui.findCase")}</Button>
      <Button onPress={onReload} isDisabled={loading}>
        {loading ? p.text("ui.loading") : p.text("ui.reloadCases")}
      </Button>
    </Form>
  );
};

export const CaseList = ({ token, onSelect, onOpen }: CaseListProps) => {
  const p = usePresentation();
  const [lookup, setLookup] = useState("");
  const request = useCallback(
    (cursor: string | null, signal: AbortSignal) => v2.list(cursor, 50, token, signal),
    [token],
  );
  const { items, cursor, message, loading, load } = useRetryablePage<
    WebV2Response<"case.list">,
    CaseSummary
  >(request, casePage);

  return (
    <section aria-labelledby="case-list-title">
      <div className="section-heading">
        <h2 id="case-list-title">{p.text("ui.cases")}</h2>
        <Button onPress={onOpen}>{p.text("ui.openNewCase")}</Button>
      </div>
      <LookupForm
        lookup={lookup}
        loading={loading}
        onChange={setLookup}
        onFind={() => {
          if (lookup !== "") onSelect(lookup);
        }}
        onReload={() => void load(null)}
      />
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
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
