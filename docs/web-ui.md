# Web UI

The [workspace components](../web/src/ui/) own the interface. Page places the heading and primary
action before the operation feedback, view tabs, toolbar and content. Section keeps its heading,
count, secondary actions and body inside one card. The outcome banner takes focus and links to the
receipt after a change.

Field owns control labels, hints, rules and errors. Text, date, select and combobox controls share
the sizing and border tokens in [field.css](../web/src/ui/field.css). The control box draws the
only border and focus ring. Its inputs and date segments stay inside the box, and its inner button
sits at the inline end with a transparent background and zero border. MoneyField joins the amount
and currency under one border. Shared rows align field labels, controls and hints. The Filters
and export buttons wrap together. On phones, the search stays in the toolbar and Filters opens a
bottom sheet.

Core supplies chip tones, table prominence, column joins, recommendations, consequences and empty
wording. DataTables use wide rows from a 64rem container, medium rows from 50rem and cards at
smaller widths. The root reserves space for classic scrollbars, so short and long lists keep the
same width and opening a drawer keeps the page behind it still. The
[layout styles](../web/src/ui/layout.css) set measures of 10rem for identifiers, 14rem for text,
12rem for people, 9rem for money and 18rem for chips. Wide and medium cells have at least 12px
between contents and align on the first text line. Medium rows stack secondary values under the
primary cell. In cards, the row menu sits at the inline end of the first line.

Text uses the workspace font. Identifiers use the mono font at the surrounding size and wrap first
at separators. Amounts break between digit groups or before the currency. A word can split when it
exceeds the width available to it. The browser matrix exercises maximum accepted values in the
[limits fixture](../web/e2e/design-matrix-fixtures.ts) at each layout boundary. Row controls stay
inside the list at every supported width.

A row recommendation uses a short core label on a secondary button. Each page and dialog allows
one solid primary action. Selected choices use the subtle accent and keep the radio indicator.

The cascade runs through reset, tokens, base, layout, components and views. The theme generator reads
[tokens.json](../web/src/theme/tokens.json). Keep authored styles in the declared layer and use logical
properties for inline and block spacing. Check keyboard focus, Arabic, both colour schemes, reduced
motion and forced colours.

L5 confines form controls to the UI components. L6 protects word wrapping. The browser matrix checks
control geometry, word integrity, primary emphasis, page offsets, minimum type size, disclosure
placement, page titles, first-row position and feedback focus. See [Verification](contributing.md#verification).
