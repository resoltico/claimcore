/* oxlint-disable jsx-a11y/no-noninteractive-tabindex -- The sole named scroll region must support keyboard scrolling, including empty tables. */
import { MoneyMeter } from "./MoneyMeter";
import { SectionsView } from "../SectionsView";
import { RowDetails } from "./RowDetails";
import { followLink } from "../../routing/followLink";
import "./table.css";
import { useId, type ReactNode } from "react";
import type { RegionContent } from "@contracts/client";
import { PresentedValue } from "../../components/PresentedValue";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";

export type RowLink = {
  href: string;
  follow: () => void;
  kind?: "ACTOR" | "REFERENCE";
  column?: string;
  label?: string;
  id?: string;
};

export const Table = ({
  view,
  timeZone = "UTC",
  renderActions,
  rowLink,
  labelledBy,
  sectionLink,
}: {
  view: Extract<RegionContent, { kind: "TABLE" }>;
  timeZone?: string;
  renderActions?: (key: string) => ReactNode;
  rowLink?: (key: string) => RowLink | undefined;
  labelledBy?: string;
  sectionLink?: RowLink;
}) => {
  const p = usePresentation();
  const tableId = useId();
  const sections = view.sections ?? [];
  const renderRow = (row: Extract<RegionContent, { kind: "TABLE" }>["rows"][number]) => {
    const link = rowLink?.(row.key);
    return (
      <tr key={row.key}>
        {row.values.map((value, position) => (
          <td
            key={view.columns[position]?.key}
            id={`${tableId}-${row.key}-${view.columns[position]?.key}`}
            data-column={view.columns[position]?.key}
            data-prominence={view.columns[position]?.prominence}
            data-money={view.columns[position]?.isMoney}
          >
            {link !== undefined &&
            (link.column === undefined
              ? value.kind === (link.kind ?? "REFERENCE")
              : view.columns[position]?.key === link.column) ? (
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
        {view.rows.some((item) => item.detail !== undefined) ? (
          <td className="table-row-detail">
            {row.detail === undefined ? null : <RowDetails value={row.detail} zone={timeZone} />}
          </td>
        ) : null}
        {renderActions === undefined ? null : (
          <td>
            <div
              className="row-actions"
              role="group"
              aria-labelledby={view.columns
                .filter((column) => column.prominence === "PRIMARY")
                .map((column) => `${tableId}-${row.key}-${column.key}`)
                .join(" ")}
            >
              {renderActions(row.key)}
            </div>
          </td>
        )}
      </tr>
    );
  };
  return (
    <>
      {view.meter === undefined ? null : (
        <MoneyMeter meter={view.meter} timeZone={timeZone} label={p.text("label.paid")} />
      )}
      <div
        className="table-view"
        role="group"
        tabIndex={0}
        {...(labelledBy === undefined
          ? { "aria-label": p.text("label.results") }
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
              {view.rows.some((item) => item.detail !== undefined) ? (
                <th scope="col">{p.text("label.recordDetails")}</th>
              ) : null}
              {renderActions === undefined ? null : (
                <th scope="col">{p.text("label.availableActs")}</th>
              )}
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
                  return row === undefined ? null : renderRow(row);
                })}
              </tbody>
            ))
          )}
        </table>
      </div>
      {sections.length === 0 ? null : (
        <SectionsView
          sections={sections}
          headingLevel={2}
          timeZone={timeZone}
          {...(sectionLink === undefined ? {} : { link: sectionLink })}
        />
      )}
    </>
  );
};

/* oxlint-enable jsx-a11y/no-noninteractive-tabindex */
