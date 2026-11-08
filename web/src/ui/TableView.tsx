/* oxlint-disable jsx-a11y/no-noninteractive-tabindex -- The sole named scroll region must support keyboard scrolling, including empty tables. */
import { SectionsView } from "./EntriesView";
import { SuppliedMessage } from "./SuppliedMessage";
import { followLink } from "../routing/followLink";
import "./presented-view.css";
import type { ReactNode } from "react";
import type { RegionContent } from "@contracts/client";
import { PresentedValue } from "../components/PresentedValue";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";

export type RowLink = {
  href: string;
  follow: () => void;
  kind?: "ACTOR" | "REFERENCE";
  label?: string;
  id?: string;
};

export const TableView = ({
  view,
  timeZone = "UTC",
  renderActions,
  rowLink,
  labelledBy,
}: {
  view: Extract<RegionContent, { kind: "TABLE" }>;
  timeZone?: string;
  renderActions?: (index: number) => ReactNode;
  rowLink?: (index: number) => RowLink | undefined;
  labelledBy?: string;
}) => {
  const p = usePresentation();
  const renderRow = (
    row: Extract<RegionContent, { kind: "TABLE" }>["rows"][number],
    index: number,
  ) => {
    const link = rowLink?.(index);
    return (
      <tr key={row.key}>
        {row.values.map((value, position) => (
          <td
            key={view.columns[position]?.key}
            data-column={view.columns[position]?.key}
            data-prominence={view.columns[position]?.prominence}
            data-money={view.columns[position]?.isMoney}
          >
            {link !== undefined && value.kind === (link.kind ?? "REFERENCE") ? (
              <a
                href={link.href}
                id={link.id}
                aria-label={link.label}
                className="row-link"
                onClick={(event) => {
                  followLink(event, link.follow);
                }}
              >
                <PresentedValue value={value} timeZone={timeZone} />
              </a>
            ) : (
              <PresentedValue value={value} timeZone={timeZone} />
            )}
          </td>
        ))}
        {renderActions === undefined ? null : <td>{renderActions(index)}</td>}
        {view.rows.some((item) => item.detail !== undefined) ? (
          <td className="table-row-detail">
            {row.detail === undefined ? null : (
              <>
                {row.detail.summary === null ? null : (
                  <p className="row-summary">
                    <SuppliedMessage message={row.detail.summary} timeZone={timeZone} />
                  </p>
                )}
                <SectionsView sections={row.detail.highlights} timeZone={timeZone} />
                <details>
                  <summary>{p.text("label.recordDetails")}</summary>
                  <SectionsView sections={row.detail.sections} timeZone={timeZone} />
                </details>
              </>
            )}
          </td>
        ) : null}
      </tr>
    );
  };
  return (
    <>
      <div
        className="table-view"
        role="group"
        tabIndex={0}
        {...(labelledBy === undefined
          ? { "aria-label": p.text("label.recordDetails") }
          : { "aria-labelledby": labelledBy })}
      >
        <table aria-labelledby={labelledBy}>
          <thead>
            <tr>
              {view.columns.map((column) => (
                <th key={column.key} scope="col" data-prominence={column.prominence}>
                  {renderCatalogKey(p, column.labelId)}
                </th>
              ))}
              {renderActions === undefined ? null : (
                <th scope="col">{p.text("label.availableActs")}</th>
              )}
              {view.rows.some((item) => item.detail !== undefined) ? (
                <th scope="col">{p.text("label.recordDetails")}</th>
              ) : null}
            </tr>
          </thead>
          {(view.groups ?? []).length === 0 ? (
            <tbody>{view.rows.map(renderRow)}</tbody>
          ) : (
            view.groups?.map((group) => (
              <tbody key={group.titleId}>
                <tr className="table-group">
                  <th
                    scope="rowgroup"
                    colSpan={
                      view.columns.length +
                      (renderActions === undefined ? 0 : 1) +
                      (view.rows.some((item) => item.detail !== undefined) ? 1 : 0)
                    }
                  >
                    {renderCatalogKey(p, group.titleId)} ·{" "}
                    <PresentedValue
                      value={{ kind: "COUNT", value: group.count }}
                      timeZone={timeZone}
                    />
                  </th>
                </tr>
                {group.rowKeys.map((key) => {
                  const index = view.rows.findIndex((row) => row.key === key);
                  const row = view.rows[index];
                  return row === undefined ? null : renderRow(row, index);
                })}
              </tbody>
            ))
          )}
        </table>
      </div>
      {(view.sections ?? []).length === 0 ? null : (
        <SectionsView sections={view.sections ?? []} timeZone={timeZone} />
      )}
    </>
  );
};

/* oxlint-enable jsx-a11y/no-noninteractive-tabindex */
