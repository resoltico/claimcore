import "./entries.css";
import type { PresentedEntry } from "@contracts/client";
import { PresentedValue } from "../../components/PresentedValue";
import { CopyValue } from "../../components/CopyValue";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { canonicalPresented } from "../../presentation/canonicalPresented";
import type { RowLink } from "./Table";
import { followLink } from "../../routing/followLink";

type Props = {
  entries: ReadonlyArray<PresentedEntry>;
  timeZone?: string;
  copy?: boolean;
  link?: RowLink;
  countLink?: RowLink;
  avatars?: boolean;
};
const EntryValue = ({
  entry,
  timeZone,
  link,
  countLink,
  avatars,
}: {
  entry: PresentedEntry;
  timeZone: string;
  link: RowLink | undefined;
  countLink: RowLink | undefined;
  avatars: boolean;
}) => {
  const p = usePresentation();
  const value = (
    <PresentedValue
      value={entry.value}
      timeZone={timeZone}
      avatar={avatars && entry.prominence === "PRIMARY"}
    />
  );
  if (
    countLink !== undefined &&
    (countLink.column === undefined ? entry.value.kind === "COUNT" : entry.key === countLink.column)
  ) {
    return (
      <a
        href={countLink.href}
        onClick={(event) => {
          followLink(event, countLink.follow);
        }}
      >
        {renderCatalogKey(p, entry.labelId)}: {value}
      </a>
    );
  }
  if (
    link !== undefined &&
    (link.column === undefined
      ? entry.value.kind === (link.kind ?? "REFERENCE")
      : entry.key === link.column)
  ) {
    return (
      <a
        href={link.href}
        onClick={(event) => {
          followLink(event, link.follow);
        }}
      >
        {value}
      </a>
    );
  }
  return value;
};
export const Entries = ({
  entries,
  timeZone = "UTC",
  copy = false,
  link,
  countLink,
  avatars = false,
}: Props) => {
  const p = usePresentation();
  return (
    <div className="entries-view">
      <dl>
        {entries.map((entry) => (
          <div
            key={entry.key}
            data-entry={entry.key}
            data-kind={entry.value.kind}
            data-prominence={entry.prominence}
            data-count-link={
              (countLink !== undefined &&
                (countLink.column === undefined
                  ? entry.value.kind === "COUNT"
                  : entry.key === countLink.column)) ||
              undefined
            }
          >
            <dt>{renderCatalogKey(p, entry.labelId)}</dt>
            <dd>
              <EntryValue
                entry={entry}
                timeZone={timeZone}
                link={link}
                countLink={countLink}
                avatars={avatars}
              />
              {copy && (entry.value.kind === "REFERENCE" || entry.value.kind === "IDENTIFIER") ? (
                <CopyValue
                  label={renderCatalogKey(p, entry.labelId)}
                  value={canonicalPresented(entry.value)}
                />
              ) : null}
            </dd>
          </div>
        ))}
      </dl>
      {copy && entries.length > 0 ? (
        <CopyValue
          label={p.text("label.recordDetails")}
          value={entries
            .map((entry) => `${entry.key}: ${canonicalPresented(entry.value)}`)
            .join("\n")}
        />
      ) : null}
    </div>
  );
};
