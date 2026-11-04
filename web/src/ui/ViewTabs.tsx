import { Tabs, TabList, Tab, TabPanel } from "react-aria-components/Tabs";
import type { ReactNode } from "react";
import "./view-tabs.css";
type Item = { id: string; label: string; count: string };
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
}) => (
  <Tabs
    selectedKey={selected}
    keyboardActivation="manual"
    onSelectionChange={(key) => {
      if (typeof key === "string" && items.some((item) => item.id === key)) {
        onSelect(key);
      }
    }}
  >
    <TabList className="view-tabs" aria-label={label} aria-busy={loading} items={items}>
      {(item) => (
        <Tab id={item.id} className="view-tab">
          {item.label} <span className="view-count">{loading ? "…" : item.count}</span>
        </Tab>
      )}
    </TabList>
    {items.map((item) => (
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
);
