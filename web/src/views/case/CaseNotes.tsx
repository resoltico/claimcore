import { useCallback, useId, useState } from "react";
import type { CaseNotesPage, NoteReadModel, ResponseByEndpoint, Screen } from "@contracts/client";
import type { CurrentCase } from "../../protocol/types";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { PresentedValue } from "../../components/PresentedValue";
import { NoticeView } from "../../presentation/Message";
import { Button } from "../../ui/Button";
import "./notes.css";

type Page = CaseNotesPage & { items: ReadonlyArray<NoteReadModel> };
const select = (reply: ResponseByEndpoint["case.notes"]): Page | null =>
  reply.outcome.kind === "SUCCEEDED"
    ? { ...reply.outcome.data, items: reply.outcome.data.notes.items }
    : null;

const noteCounts = (view: Screen) => {
  const content = view.regions.find((item) => item.role === "NOTES")?.content;
  const count =
    content?.kind === "TABLE"
      ? (content.sections ?? [])
          .flatMap((section) => section.entries)
          .find((entry) => entry.key === "notesCount")?.value
      : undefined;
  return {
    shown: content?.kind === "TABLE" ? content.rows.length : 0,
    total: count?.kind === "COUNT" ? count.value : null,
  };
};
const NotesShown = ({ view, shown }: { view: Screen; shown: number }) => {
  const p = usePresentation();
  const { total } = noteCounts(view);
  return total === null ? null : (
    <p>{p.text("ui.notesShown", { shown: p.integer(shown), total: p.integer(total) })}</p>
  );
};
const pageKey = (view: Screen): string => {
  const content = view.regions.find((item) => item.role === "NOTES")?.content;
  return content?.kind === "TABLE" ? (content.rows[0]?.key ?? "empty") : "empty";
};
const NoteRows = ({
  view,
  items,
  zone,
  onRemove,
}: {
  view: Screen;
  items: ReadonlyArray<NoteReadModel>;
  zone: string;
  onRemove?: (id: string) => void;
}) => {
  const p = usePresentation();
  const id = useId();
  const content = view.regions.find((region) => region.role === "NOTES")?.content;
  if (content?.kind !== "TABLE") {
    return null;
  }
  return (
    <ul className="note-list">
      {content.rows.map((row) => (
        <li key={row.key}>
          <div className="note-author" id={`${id}-${row.key}`}>
            {row.values.map((value, index) =>
              content.columns[index]?.key === "noteText" ? null : (
                <span key={content.columns[index]?.key}>
                  <PresentedValue value={value} timeZone={zone} />
                </span>
              ),
            )}
          </div>
          {row.values.map((value, index) =>
            content.columns[index]?.key === "noteText" ? (
              <div className="note-text" key={content.columns[index]?.key}>
                <PresentedValue value={value} timeZone={zone} />
              </div>
            ) : null,
          )}
          {onRemove !== undefined &&
          items.some((item) => item.noteId === row.key && item.canRemove) ? (
            <div role="group" aria-labelledby={`${id}-${row.key}`}>
              <Button
                variant="secondary"
                onPress={() => {
                  onRemove(row.key);
                }}
              >
                {p.text("ui.removeNote")}
              </Button>
            </div>
          ) : null}
        </li>
      ))}
    </ul>
  );
};
const AllNotes = ({
  current,
  onRemove,
  onDisclosureRefused,
}: {
  current: CurrentCase;
  onRemove?: (id: string) => void;
  onDisclosureRefused: () => void;
}) => {
  const p = usePresentation();
  const input = useCallback(
    (cursor: string | null) => ({
      target: { kind: "ID" as const, caseId: current.caseId },
      limit: 20,
      ...(cursor === null ? {} : { cursor }),
    }),
    [current.caseId],
  );
  const result = useEndpointPage<"case.notes", NoteReadModel, Page>(
    "case.notes",
    input,
    select,
    onDisclosureRefused,
  );
  return (
    <>
      {result.views.map((page) => (
        <NoteRows
          key={pageKey(page.view)}
          view={page.view}
          items={result.items}
          zone={page.basis.timeZone}
          {...(onRemove === undefined ? {} : { onRemove })}
        />
      ))}
      {result.message === null ? null : (
        <p role="alert">
          <NoticeView value={result.message} />
        </p>
      )}
      {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      <NotesShown
        view={result.views.at(-1)?.view ?? current.view}
        shown={result.views.reduce((count, page) => count + noteCounts(page.view).shown, 0)}
      />
      {result.cursor === null && result.message === null ? null : (
        <Button
          variant="secondary"
          isDisabled={result.loading}
          onPress={() => {
            void result.load(result.cursor);
          }}
        >
          {p.text(result.message === null ? "ui.moreNotes" : "ui.tryAgain")}
        </Button>
      )}
    </>
  );
};
export const CaseNotes = ({
  current,
  editing,
  onAdd,
  onRemove,
  onDisclosureRefused,
}: {
  current: CurrentCase;
  editing: boolean;
  onAdd: (text: string) => void;
  onRemove: (id: string) => void;
  onDisclosureRefused: () => void;
}) => {
  const p = usePresentation();
  const id = useId();
  const [text, setText] = useState("");
  const [expanded, setExpanded] = useState(false);
  const region = current.view.regions.find((item) => item.role === "NOTES");
  if (region === undefined) {
    return null;
  }
  return (
    <section
      className="screen-region case-notes"
      data-region="NOTES"
      aria-labelledby={`${id}-title`}
    >
      <h2 id={`${id}-title`}>{renderCatalogKey(p, region.titleId ?? "section.notes.label")}</h2>
      {current.notes.canAdd ? (
        <div className="note-composer">
          <label htmlFor={`${id}-text`}>{p.fieldLabel("noteText")}</label>
          <textarea
            id={`${id}-text`}
            value={text}
            placeholder={p.text("ui.notePlaceholder")}
            disabled={editing}
            onChange={(event) => {
              setText(event.target.value);
            }}
          />
          <Button
            variant="secondary"
            isDisabled={editing || text.length === 0}
            onPress={() => {
              onAdd(text);
            }}
          >
            {p.text("act.ADD_NOTE.label")}
          </Button>
        </div>
      ) : null}
      {expanded ? (
        <AllNotes
          current={current}
          onDisclosureRefused={onDisclosureRefused}
          {...(editing ? {} : { onRemove })}
        />
      ) : (
        <>
          <NoteRows
            view={current.view}
            items={current.notes.items}
            zone={current.work.basis.timeZone}
            {...(editing ? {} : { onRemove })}
          />
          <NotesShown view={current.view} shown={noteCounts(current.view).shown} />
          {current.notes.count > current.notes.items.length ? (
            <Button
              variant="secondary"
              onPress={() => {
                setExpanded(true);
              }}
            >
              {p.text("ui.showAllNotes")}
            </Button>
          ) : null}
        </>
      )}
    </section>
  );
};
