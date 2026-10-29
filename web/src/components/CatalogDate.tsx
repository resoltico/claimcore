import { parseDate } from "@internationalized/date";
import { DateField, DateInput, DateSegment } from "react-aria-components/DateField";
import { Calendar, CalendarGrid, CalendarCell } from "react-aria-components/Calendar";
import { Button } from "react-aria-components/Button";
import { DialogTrigger, Dialog } from "react-aria-components/Dialog";
import { Heading } from "react-aria-components/Heading";
import { Popover } from "react-aria-components/ComboBox";
import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import type { FieldCatalogEntry } from "../protocol/types";
import type { Notice } from "../protocol/notices";
import { usePresentation } from "../presentation/context";
const dateValue = (value: string) => {
  try {
    return parseDate(value);
  } catch {
    return null;
  }
};
export type TypedFieldProps = {
  field: FieldCatalogEntry;
  value: string;
  inputName: string;
  disabled: boolean;
  error?: Notice | undefined;
  onChange: (value: string) => void;
};
const CalendarPopup = ({ field, value, disabled, onChange }: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <DialogTrigger>
      <Button
        isDisabled={disabled}
        aria-label={`${p.text("ui.calendar")}: ${p.fieldLabel(field.name)}`}
      >
        ▦
      </Button>
      <Popover className="calendar-popover">
        <Dialog aria-label={p.fieldLabel(field.name)}>
          <Calendar
            value={dateValue(value)}
            onChange={(next) => {
              onChange(next.toString());
            }}
          >
            <header>
              <Button slot="previous" aria-label={p.text("ui.previousMonth")}>
                ‹
              </Button>
              <Heading />
              <Button slot="next" aria-label={p.text("ui.nextMonth")}>
                ›
              </Button>
            </header>
            <CalendarGrid>{(day) => <CalendarCell date={day} />}</CalendarGrid>
          </Calendar>
        </Dialog>
      </Popover>
    </DialogTrigger>
  );
};
const DateEntry = ({ field, value, inputName, disabled, error, onChange }: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <DateField
      id={`field-${inputName}`}
      name={inputName}
      className="text-field"
      data-field-name={field.name}
      value={dateValue(value)}
      onChange={(next) => {
        onChange(next?.toString() ?? "");
      }}
      isDisabled={disabled}
      isInvalid={error !== undefined}
      validationBehavior="aria"
    >
      <Label>{p.fieldLabel(field.name)}</Label>
      <DateInput aria-invalid={error !== undefined || undefined}>
        {(segment) => <DateSegment segment={segment} />}
      </DateInput>
      <Text slot="description" className="field-description">
        <small>{p.fieldMeaning(field.name)}</small>
        <small>{p.hint(field)}</small>
      </Text>
      {error === undefined ? null : <FieldError>{p.notice(error)}</FieldError>}
    </DateField>
  );
};
export const CatalogDate = (props: TypedFieldProps) => (
  <div className="date-control">
    <DateEntry {...props} />
    <CalendarPopup {...props} />
  </div>
);
