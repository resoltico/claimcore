import { useEffect, useRef, useState } from "react";
import { ComboBox, Popover } from "react-aria-components/ComboBox";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { ListBox, ListBoxItem } from "react-aria-components/ListBox";
import type { CaseSummary, PresentedScalar_STATE } from "@contracts/client";
import { apiEndpoints, requestEndpoint, resultNotice, service } from "../api/service";
import { useSessionContext } from "../session/SessionContext";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import type { Notice } from "../protocol/notices";
import { productMetadata } from "../protocol/productMetadata";
import { casePath } from "../routing/routes";
import { navigate } from "../routing/navigation";
import { worklistPath } from "../routing/worklistQuery";
import { Chip } from "../ui/Chip";
import { Button } from "../ui/Button";
import { Dialog } from "../ui/Dialog";
import "./find-case.css";

type Suggestion = {
  id: string;
  path: string;
  reference: string;
  claimant: string | null;
  state: PresentedScalar_STATE | null;
};

const suggestionsFrom = (rows: ReadonlyArray<CaseSummary>): ReadonlyArray<Suggestion> =>
  rows.map((row) => ({
    id: row.caseId,
    path: casePath(row.caseId),
    reference: row.caseReference,
    claimant: row.state.kind === "ERASED" ? null : (row.state.parties.CLAIMANT?.[0] ?? null),
    state: { kind: "STATE", vocabulary: "headline", token: row.headline },
  }));

const referenceField = productMetadata.catalog.fields.find(
  ({ valueKind }) => valueKind === "REFERENCE",
);
const referenceGrammar =
  referenceField?.scalar.kind === "TEXT" ? referenceField.scalar.format.grammar : null;
const isExactReference = (query: string) =>
  referenceGrammar !== null && new RegExp(`^(?:${referenceGrammar})$`, "u").test(query);

const FindControl = ({ close }: { close: () => void }) => {
  const p = usePresentation();
  const { state } = useSessionContext();
  const credential = state.kind === "authenticated" ? state.token : null;
  const [query, setQuery] = useState({ value: "", attempt: 0 });
  const [results, setResults] = useState<ReadonlyArray<Suggestion>>([]);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [suggestionFailed, setSuggestionFailed] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const flight = useRef<Promise<void>>(Promise.resolve());
  const search = query.value;

  useEffect(() => {
    const request = new AbortController();
    controller.current = request;
    const text = query.value.trim();
    if (credential === null || text.length < 2) {
      return () => {
        request.abort();
      };
    }
    const timer = window.setTimeout(() => {
      flight.current = flight.current.then(async () => {
        if (request.signal.aborted) {
          return;
        }
        const result = await requestEndpoint(
          apiEndpoints.caseList,
          credential,
          { view: "ALL", text, limit: 8 },
          request.signal,
          undefined,
          "DATA",
        );
        if (!request.signal.aborted) {
          if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
            setResults(suggestionsFrom(result.value.outcome.data.items));
          } else {
            setSuggestionFailed(true);
          }
        }
      });
    }, 250);
    return () => {
      window.clearTimeout(timer);
      request.abort();
    };
  }, [query, credential]);

  const open = (path: string) => {
    if (navigate(path)) {
      close();
    }
  };
  const exactOrFilter = async () => {
    if (search.trim() === "" || credential === null) {
      return;
    }
    if (!isExactReference(search)) {
      open(worklistPath({ view: "ALL", text: search }));
      return;
    }
    controller.current?.abort();
    const exact = new AbortController();
    controller.current = exact;
    await flight.current;
    if (exact.signal.aborted) {
      return;
    }
    const result = await service.get(search, credential, exact.signal);
    if (exact.signal.aborted) {
      return;
    }
    if (
      result.kind === "outcome" &&
      result.value.outcome.kind === "SUCCEEDED" &&
      result.value.outcome.document.subject.kind === "CASE"
    ) {
      open(casePath(result.value.outcome.document.subject.caseId));
    } else {
      setNotice(resultNotice(result));
    }
  };

  useEffect(
    () => () => {
      controller.current?.abort();
    },
    [],
  );

  return (
    <div className="find-control">
      <ComboBox
        inputValue={query.value}
        onInputChange={(value) => {
          setQuery({ value, attempt: 0 });
          setNotice(null);
          setSuggestionFailed(false);
          setResults([]);
        }}
        items={results}
        allowsCustomValue
        menuTrigger="focus"
        onChange={(key) => {
          const selection = results.find(({ id }) => id === key);
          if (selection !== undefined) {
            open(selection.path);
          }
        }}
      >
        <Label>{p.text("ui.findCase")}</Label>
        <Input
          className="find-input"
          onKeyDownCapture={(event) => {
            if (
              event.key === "Enter" &&
              event.currentTarget.getAttribute("aria-activedescendant") === null
            ) {
              event.preventDefault();
              event.stopPropagation();
              void exactOrFilter();
            }
          }}
        />
        <Popover className="find-options" placement="bottom start">
          <ListBox<Suggestion>>
            {(item) => (
              <ListBoxItem id={item.id} textValue={item.reference} className="find-option">
                <bdi className="identifier-value">{item.reference}</bdi>
                {item.claimant === null ? null : <bdi>{item.claimant}</bdi>}
                {item.state === null ? null : <Chip value={item.state} />}
              </ListBoxItem>
            )}
          </ListBox>
        </Popover>
      </ComboBox>
      {suggestionFailed ? (
        <Button
          variant="quiet"
          compact
          onPress={() => {
            setSuggestionFailed(false);
            setQuery(({ value, attempt }) => ({ value, attempt: attempt + 1 }));
          }}
        >
          {p.text("ui.tryAgain")}
        </Button>
      ) : null}
      {notice === null ? null : (
        <p role="alert">
          <NoticeView value={notice} />
        </p>
      )}
    </div>
  );
};

export const FindCase = ({
  open,
  setOpen,
}: {
  open: boolean;
  setOpen: (open: boolean) => void;
}) => {
  const p = usePresentation();
  return (
    <Dialog
      title={p.text("ui.findCase")}
      closeLabel={p.text("ui.close")}
      isOpen={open}
      isDismissable
      onOpenChange={setOpen}
    >
      {open ? (
        <FindControl
          close={() => {
            setOpen(false);
          }}
        />
      ) : null}
    </Dialog>
  );
};
