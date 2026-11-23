import { Page } from "../ui/Page";
import { TextInput } from "../ui/TextField";
import { useCallback, useState } from "react";
import type { RegionRow, RequestByEndpoint } from "@contracts/client";
import { termKeys } from "@contracts/messages";
import { apiEndpoints, type ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { isTerm } from "../presentation/terms";
import { ListFooter } from "../ui/ListFooter";
import { Toolbar } from "../ui/Toolbar";
import { ResultPages } from "../ui/ResultPages";
import { Select } from "../ui/Select";

const endpoint = apiEndpoints.disclosureTrail;
type TrailSelection = Omit<RequestByEndpoint[typeof endpoint], "cursor" | "limit">;

const select = (reply: ServiceResponse<typeof endpoint>) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const { document } = reply.outcome;
  if (document.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: document.screen.regions.flatMap((region) =>
      region.content.kind === "TABLE" ? region.content.rows : [],
    ),
    nextCursor: document.nextCursor,
    basis: document.basis.observation,
    screen: document.screen,
    disclosure: document.disclosure,
  };
};

const textField = (data: FormData, name: string) => {
  const value = data.get(name);
  return typeof value === "string" && value.trim() !== "" ? value.trim() : undefined;
};

const trailSelection = (form: HTMLFormElement, kind: string): TrailSelection => {
  const fields = new FormData(form);
  const actorId = textField(fields, "actorId");
  const caseId = textField(fields, "caseId");
  const from = textField(fields, "from");
  const until = textField(fields, "until");
  return {
    ...(actorId === undefined ? {} : { actorId }),
    ...(caseId === undefined ? {} : { caseId }),
    ...(from === undefined ? {} : { from }),
    ...(until === undefined ? {} : { until }),
    ...(isTerm("disclosureKind", kind) ? { kind } : {}),
  };
};

const trailKinds = Object.keys(termKeys.disclosureKind).filter((kind) =>
  isTerm("disclosureKind", kind),
);

const TrailFilters = ({ apply }: { apply: (selection: TrailSelection) => void }) => {
  const p = usePresentation();
  const [kind, setKind] = useState("");
  return (
    <form
      aria-label={p.text("endpoint.disclosure.trail.label")}
      onSubmit={(event) => {
        event.preventDefault();
        apply(trailSelection(event.currentTarget, kind));
      }}
    >
      <Toolbar>
        <div className="text-field">
          <label htmlFor="trail-actor">{p.text("input.actorId.label")}</label>
          <TextInput id="trail-actor" name="actorId" type="text" autoComplete="off" dir="ltr" />
        </div>
        <div className="text-field">
          <label htmlFor="trail-case">{p.text("input.disclosureCaseId.label")}</label>
          <TextInput id="trail-case" name="caseId" type="text" autoComplete="off" dir="ltr" />
        </div>
        <Select
          id="trail-kind"
          label={p.text("input.disclosureKind.label")}
          value={kind}
          options={[
            { value: "", label: p.text("label.disclosureAnyKind") },
            ...trailKinds.map((eventKind) => ({
              value: eventKind,
              label: p.term("disclosureKind", eventKind),
            })),
          ]}
          onChange={setKind}
        />
        <div className="text-field">
          <label htmlFor="trail-from">{p.text("input.disclosureFrom.label")}</label>
          <TextInput
            id="trail-from"
            name="from"
            type="text"
            autoComplete="off"
            dir="ltr"
            aria-describedby="trail-time-format"
          />
        </div>
        <div className="text-field">
          <label htmlFor="trail-until">{p.text("input.disclosureUntil.label")}</label>
          <TextInput
            id="trail-until"
            name="until"
            type="text"
            autoComplete="off"
            dir="ltr"
            aria-describedby="trail-time-format"
          />
        </div>
        <p id="trail-time-format">{p.text("input.disclosureFrom.hint")}</p>
        <button type="submit">{p.text("ui.applyFilters")}</button>
      </Toolbar>
    </form>
  );
};

type TrailPage = NonNullable<ReturnType<typeof select>>;
export const DisclosureTrail = () => {
  const p = usePresentation();
  const [selection, setSelection] = useState<TrailSelection>({});
  const input = useCallback(
    (cursor: string | null) => ({
      ...selection,
      limit: 50,
      ...(cursor === null ? {} : { cursor }),
    }),
    [selection],
  );
  const page = useEndpointPage<typeof endpoint, RegionRow, TrailPage>(endpoint, input, select);
  const load = (cursor: string) => {
    document.getElementById("disclosure-trail-title")?.focus({ preventScroll: true });
    void page.load(cursor);
  };
  return (
    <Page
      id="disclosure-trail-title"
      title={p.text("endpoint.disclosure.trail.label")}
      className="disclosure-trail"
    >
      <TrailFilters apply={setSelection} />
      {page.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      <ResultPages views={page.views} />
      <ListFooter
        basis={page.page?.basis ?? null}
        continuation={page.cursor}
        loading={page.loading}
        busy={page.refreshBusy}
        load={load}
        refresh={
          page.page === null || page.message !== null
            ? () => {
                void page.load(null);
              }
            : page.refresh
        }
      />
    </Page>
  );
};
