import { useState } from "react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { Select } from "../../../src/ui/Select";
import { IconSprite } from "../../../src/ui/icons";

const options = [
  { value: "en-GB", label: "English" },
  { value: "lv-LV", label: "Latviešu" },
  { value: "de-DE", label: "Deutsch" },
];
const Example = ({
  changed,
  disabled = false,
  submitted,
}: {
  changed: (value: string) => void;
  disabled?: boolean;
  submitted?: () => void;
}) => {
  const [value, setValue] = useState("en-GB");
  return (
    <>
      <IconSprite />
      <form
        aria-label="Preferences"
        onSubmit={(event) => {
          event.preventDefault();
          submitted?.();
        }}
      >
        <Select
          label="Format"
          value={value}
          options={options}
          disabled={disabled}
          onChange={(next) => {
            changed(next);
            setValue(next);
          }}
        />
      </form>
    </>
  );
};
it("supports keyboard opening, typeahead, commit and cancellation without native select", async () => {
  const user = userEvent.setup();
  const changed = vi.fn();
  const submitted = vi.fn();
  render(<Example changed={changed} submitted={submitted} />);
  const trigger = screen.getByRole("combobox", { name: "Format" });
  expect(document.querySelector("select")).toBeNull();
  expect(trigger).toHaveValue("en-GB");
  expect(trigger).not.toHaveFocus();
  expect(screen.queryByRole("listbox")).toBeNull();
  await user.tab();
  await user.keyboard("{ArrowDown}");
  expect(screen.getByRole("listbox", { name: "Format" })).toBeVisible();
  await user.keyboard("lat");
  await waitFor(() => {
    expect(screen.getByRole("option", { name: "Latviešu" })).toHaveFocus();
  });
  await user.keyboard("{Enter}");
  expect(changed).toHaveBeenLastCalledWith("lv-LV");
  expect(trigger).toHaveValue("lv-LV");
  await waitFor(() => {
    expect(trigger).toHaveFocus();
  });
  await user.keyboard("{ArrowDown}{End}{Escape}");
  expect(screen.queryByRole("listbox")).toBeNull();
  expect(trigger).toHaveValue("lv-LV");
  await waitFor(() => {
    expect(trigger).toHaveFocus();
  });
  expect(changed).toHaveBeenCalledOnce();
  expect(submitted).not.toHaveBeenCalled();
});
it("opens a matching choice from closed typeahead and commits only that closed value", async () => {
  const user = userEvent.setup();
  const changed = vi.fn();
  render(<Example changed={changed} />);
  await user.tab();
  await user.keyboard("d{Enter}");
  expect(changed).toHaveBeenLastCalledWith("de-DE");
  expect(screen.getByRole("combobox", { name: "Format" })).toHaveValue("de-DE");
});
it("cannot open or change a disabled field", async () => {
  const changed = vi.fn();
  render(<Example changed={changed} disabled />);
  const trigger = screen.getByRole("combobox", { name: "Format" });
  expect(trigger).toBeDisabled();
  await userEvent.setup().click(trigger);
  expect(screen.queryByRole("listbox")).toBeNull();
  expect(changed).not.toHaveBeenCalled();
});

it("commits an explicit empty-value choice as a value rather than losing the selection", async () => {
  const changed = vi.fn();
  render(
    <Select
      label="Filter"
      value="en-GB"
      options={[{ value: "", label: "Any" }, ...options]}
      onChange={changed}
    />,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: "Filter" }));
  await user.click(screen.getByRole("option", { name: "Any" }));
  expect(changed).toHaveBeenCalledWith("");
  expect(screen.queryByRole("listbox")).toBeNull();
});

it("opens at the requested boundary and cancels without committing the focused candidate", async () => {
  const user = userEvent.setup();
  const changed = vi.fn();
  render(<Example changed={changed} />);
  const trigger = screen.getByRole("combobox", { name: "Format" });
  await user.tab();
  await user.keyboard("{End}");
  await waitFor(() => expect(screen.getByRole("option", { name: "Deutsch" })).toHaveFocus());
  await user.keyboard("{Escape}");
  await waitFor(() => expect(trigger).toHaveFocus());
  expect(trigger).toHaveValue("en-GB");
  expect(changed).not.toHaveBeenCalled();
  await user.keyboard("{Home}");
  await waitFor(() => expect(screen.getByRole("option", { name: "English" })).toHaveFocus());
  await user.keyboard("{ArrowDown}{Enter}");
  expect(changed).toHaveBeenCalledExactlyOnceWith("lv-LV");
});
