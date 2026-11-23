import { Menu, MenuItem, MenuSection, MenuTrigger } from "react-aria-components/Menu";
import type { ReactNode } from "react";
import { Header } from "react-aria-components/Header";
import { Popover } from "react-aria-components/ComboBox";
import { Button } from "./Button";
import "./action-menu.css";

type Item = {
  id: string;
  label: string;
  disabled?: boolean;
  consequence?: "ROUTINE" | "STEWARDSHIP" | "IRREVERSIBLE" | null;
  select: () => void;
};

export const ActionMenu = ({
  label,
  items,
  stewardshipLabel,
  icon = false,
  trigger,
  id,
  disabled = false,
}: {
  label: string;
  items: ReadonlyArray<Item>;
  stewardshipLabel?: string;
  icon?: boolean;
  trigger?: ReactNode;
  id?: string;
  disabled?: boolean;
}) => {
  const routine = items.filter(
    ({ consequence }) => consequence !== "STEWARDSHIP" && consequence !== "IRREVERSIBLE",
  );
  const stewardship = items.filter(
    ({ consequence }) => consequence === "STEWARDSHIP" || consequence === "IRREVERSIBLE",
  );
  const renderItem = (item: Item) => (
    <MenuItem
      key={item.id}
      id={item.id}
      className="action-menu-item"
      textValue={item.label}
      isDisabled={item.disabled ?? false}
      data-consequence={item.consequence ?? undefined}
      onAction={item.select}
    >
      {item.label}
    </MenuItem>
  );
  return (
    <MenuTrigger>
      <Button
        {...(id === undefined ? {} : { id })}
        variant="quiet"
        compact
        aria-label={label}
        isDisabled={disabled}
      >
        {trigger ?? (icon ? <span aria-hidden="true">⋯</span> : label)}
      </Button>
      <Popover className="action-menu-popover" placement="bottom end">
        <Menu className="action-menu" aria-label={label}>
          {routine.map(renderItem)}
          {stewardship.length === 0 ? null : (
            <MenuSection className="action-menu-stewardship">
              {stewardshipLabel === undefined ? null : <Header>{stewardshipLabel}</Header>}
              {stewardship.map(renderItem)}
            </MenuSection>
          )}
        </Menu>
      </Popover>
    </MenuTrigger>
  );
};
