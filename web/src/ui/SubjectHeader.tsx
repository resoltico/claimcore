import type { ReactNode } from "react";
import type { Region } from "@contracts/client";
import { PresentedValue } from "../components/PresentedValue";
import type { ChipValue } from "./Chip";
import { ChipList, chipValues } from "./ChipList";
import { Entries } from "./regions/Entries";
import "./subject-header.css";

export const SubjectHeader = ({
  region,
  timeZone,
  fallback,
  renderTitle,
  actions,
  renderTitleLine,
}: {
  region: Region | undefined;
  timeZone: string;
  fallback?: string | undefined;
  renderTitle: (value: ReactNode, text: string | undefined) => ReactNode;
  actions?: ReactNode;
  renderTitleLine?: (line: ReactNode) => ReactNode;
}) => {
  const entries = region?.content.kind === "ENTRIES" ? region.content.entries : [];
  const reference = entries.find(({ value }) => value.kind === "REFERENCE");
  const chips = entries.flatMap<ChipValue>(({ value }) => {
    if (value.kind === "STATE") {
      return [value];
    }
    return value.kind === "CODES" ? chipValues(value) : [];
  });
  const facts = entries.filter(
    (entry) =>
      entry !== reference &&
      entry.prominence === "PRIMARY" &&
      !["STATE", "CODE", "CODES", "ABSENT"].includes(entry.value.kind),
  );
  const text = reference?.value.kind === "REFERENCE" ? reference.value.value : fallback;
  const heading =
    text === undefined ? null : (
      <PresentedValue value={{ kind: "REFERENCE", value: text }} timeZone={timeZone} />
    );
  const line = (
    <div className="case-title-line">
      {renderTitle(heading, text)}
      <ChipList values={chips} />
      {actions === undefined ? null : <div className="subject-header-actions">{actions}</div>}
    </div>
  );
  return (
    <>
      {renderTitleLine === undefined ? line : renderTitleLine(line)}
      {facts.length === 0 ? null : (
        <div className="subject-header-facts">
          <Entries entries={facts} timeZone={timeZone} />
        </div>
      )}
    </>
  );
};
