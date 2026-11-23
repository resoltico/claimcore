import { Tabs, TabList, Tab, TabPanel } from "react-aria-components/Tabs";
import type { ReactNode } from "react";
import { usePresentation } from "../presentation/context";
import { ActionMenu } from "./ActionMenu";
import "./view-tabs.css";
type Item = {
  id: string;
  label: ReactNode;
  count: ReactNode;
  prominence?: "PRIMARY" | "SECONDARY" | "DETAIL" | undefined;
  menuLabel?: string | undefined;
};
/** Ordered choices and counts belong to the caller's core snapshot. Arrow keys move focus; Enter commits. */
export const ViewTabs = ({
  items,
  selected,
  label,
  loading,
  onSelect,
  children,
  controls,
}: {
  items: ReadonlyArray<Item>;
  selected: string;
  label: string;
  loading: boolean;
  onSelect: (id: string) => void;
  children: ReactNode;
  controls: ReactNode;
}) => {
  const p = usePresentation();
  const tabs = items.filter(
    (item) => item.id === selected || (item.prominence ?? "PRIMARY") === "PRIMARY",
  );
  const more = items.filter(
    (item) => item.prominence === "SECONDARY" && item.menuLabel !== undefined,
  );
  return (
    <>
      <Tabs
        selectedKey={selected}
        keyboardActivation="manual"
        onSelectionChange={(key) => {
          if (typeof key === "string" && items.some((item) => item.id === key)) {
            onSelect(key);
          }
        }}
      >
        <div className="view-tabs-bar">
          <TabList className="view-tabs" aria-label={label} aria-busy={loading} items={tabs}>
            {(item) => (
              <Tab id={item.id} className="view-tab">
                {item.label} <span className="view-count">{loading ? "…" : item.count}</span>
              </Tab>
            )}
          </TabList>
          {more.length === 0 ? null : (
            <ActionMenu
              label={p.text("ui.moreViews")}
              items={more.map((item) => ({
                id: item.id,
                label: item.menuLabel ?? item.id,
                select: () => {
                  onSelect(item.id);
                },
              }))}
            />
          )}
        </div>
        {tabs.map((item) => (
          <TabPanel key={item.id} id={item.id} aria-label={label} className="view-tab-panel">
            {selected === item.id ? (
              <>
                {controls}
                {children}
              </>
            ) : null}
          </TabPanel>
        ))}
      </Tabs>
      {selected === "" ? (
        <div className="view-tab-panel">
          {controls}
          {children}
        </div>
      ) : null}
    </>
  );
};
