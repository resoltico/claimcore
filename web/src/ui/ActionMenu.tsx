import { Menu, MenuItem, MenuSection, MenuTrigger } from "react-aria-components/Menu";
import { useId, type ReactNode } from "react";
import { Header } from "react-aria-components/Header";
import { Popover } from "react-aria-components/ComboBox";
import { Button } from "./Button";
import "./action-menu.css";

type Item = {
  id: string;
  label: string;
  disabled?: boolean;
  description?: ReactNode;
  consequence?: "ROUTINE" | "STEWARDSHIP" | "IRREVERSIBLE" | null;
  select: () => void;
};

const ActionMenuItem = ({ item }: { item: Item }) => {
  const description = useId();
  return (
    <MenuItem
      key={item.id}
      id={item.id}
      className="action-menu-item"
      textValue={item.label}
      aria-label={item.label}
      aria-describedby={item.description === undefined ? undefined : description}
      isDisabled={item.disabled ?? false}
      data-consequence={item.consequence ?? undefined}
      data-menu-item={item.id}
      onAction={item.select}
    >
      <span>{item.label}</span>
      {item.description === undefined ? null : (
        <span id={description} className="action-menu-description">
          {item.description}
        </span>
      )}
    </MenuItem>
  );
};

export const ActionMenu = ({
  label,
  items,
  stewardshipLabel,
  icon = false,
  trigger,
  id,
  disabled = false,
  variant = "quiet",
  compact = true,
}: {
  label: string;
  items: ReadonlyArray<Item>;
  stewardshipLabel?: string;
  icon?: boolean;
  trigger?: ReactNode;
  id?: string;
  disabled?: boolean;
  variant?: "primary" | "secondary" | "quiet";
  compact?: boolean;
}) => {
  const routine = items.filter(
    ({ consequence }) => consequence !== "STEWARDSHIP" && consequence !== "IRREVERSIBLE",
  );
  const stewardship = items.filter(
    ({ consequence }) => consequence === "STEWARDSHIP" || consequence === "IRREVERSIBLE",
  );
  return (
    <MenuTrigger>
      <Button
        {...(id === undefined ? {} : { id })}
        variant={variant}
        compact={compact}
        aria-label={label}
        isDisabled={disabled}
      >
        {trigger ?? (icon ? <span aria-hidden="true">⋯</span> : label)}
      </Button>
      <Popover className="action-menu-popover" placement="bottom end">
        <Menu className="action-menu" aria-label={label}>
          {routine.map((item) => (
            <ActionMenuItem key={item.id} item={item} />
          ))}
          {stewardship.length === 0 ? null : (
            <MenuSection className="action-menu-stewardship">
              {stewardshipLabel === undefined ? null : <Header>{stewardshipLabel}</Header>}
              {stewardship.map((item) => (
                <ActionMenuItem key={item.id} item={item} />
              ))}
            </MenuSection>
          )}
        </Menu>
      </Popover>
    </MenuTrigger>
  );
};
