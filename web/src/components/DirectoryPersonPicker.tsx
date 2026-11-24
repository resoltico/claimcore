import { ComboBox, type PickerItem } from "../ui/ComboBox";
import { apiEndpoints } from "../api/service";
import { actorName } from "../presentation/actorName";
import { personLinkActorId } from "../routing/coreLinks";
import { isCanonicalUuid } from "../protocol/uuid";
import { useEffect, useState } from "react";
import { useEndpoint } from "../hooks/useEndpoint";
import { usePresentation } from "../presentation/context";
import type { HumanDocument } from "@contracts/client";

const directoryChoices = (
  document: HumanDocument,
  p: ReturnType<typeof usePresentation>,
): PickerItem[] =>
  document.screen.regions.flatMap(({ content }) => {
    if (content.kind !== "TABLE") {
      return [];
    }
    return content.rows.flatMap((row) => {
      const actor = row.values.find((value) => value.kind === "ACTOR");
      const id = row.link === undefined ? undefined : personLinkActorId(row.link);
      return actor?.kind === "ACTOR" && id !== undefined
        ? [{ id, label: actorName(p, actor.value) }]
        : [];
    });
  });

export const DirectoryPersonPicker = ({
  selected,
  change,
}: {
  selected: string;
  change: (id: string) => void;
}) => {
  const p = usePresentation();
  const [draft, setDraft] = useState(selected);
  const [query, setQuery] = useState("");
  const exact = draft.trim().toLowerCase();
  const isExact = isCanonicalUuid(exact);
  useEffect(() => {
    if (draft.trim().length < 2 || isExact) {
      return;
    }
    const delay = window.setTimeout(() => {
      setQuery(draft.trim());
    }, 250);
    return () => {
      window.clearTimeout(delay);
    };
  }, [draft, isExact]);
  const searching = query === draft.trim() && query !== "" && !isExact;
  const list = useEndpoint(apiEndpoints.peopleList, { text: query, limit: 8 }, searching);
  const document = list.value?.outcome.kind === "SUCCEEDED" ? list.value.outcome.document : null;
  const choices: PickerItem[] = searching && document !== null ? directoryChoices(document, p) : [];
  if (isExact) {
    choices.push({ id: exact, label: p.text("label.useAccountId", { id: exact }) });
  }
  const clearOrUseId = () => {
    change(isExact ? exact : "");
  };
  return (
    <ComboBox
      id="history-actor"
      label={p.text("label.person")}
      value={draft}
      items={choices}
      onChange={setDraft}
      onSelect={change}
      onSubmit={isExact || draft.trim() === "" ? clearOrUseId : undefined}
    />
  );
};
