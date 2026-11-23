# Web UI

ClaimCore's views compose the building blocks in [web/src/ui](../web/src/ui/). Use Page and Section for
headings and structure, DataTable for supplied rows, ChipList for codes and ActionMenu for grouped
actions. Use Dialog for important disclosures and Drawer-style dialogs for editing. StatusBanner
keeps an outcome visible and gives focus to the result.

The cascade order is reset, tokens, base, layout, components and views. Each stylesheet declares its
layer. The theme generator reads [tokens.json](../web/src/theme/tokens.json). Vite loads its generated
CSS at build time. View styles arrange their own content. Shared control geometry, spacing, type and
colours use the theme's semantic tokens.

Core supplies chip tones, table prominence, recommendation, consequences and empty-state wording.
Lists show primary and secondary columns. Secondary values stack beneath the first primary cell in a
narrow table. Detail columns stay in the supplied document for other presentations. Use Core labels
for the row and the actions. Keep reference links keyboard reachable and leave action controls above
the row's linked hit area.

Use logical properties, visible focus and text labels. Check narrow layouts, Arabic, both colour
schemes, reduced motion and forced colours. Menus and dialogs use React Aria keyboard and focus
behaviour. Keep authored styles in CSS. Add a component stylesheet to the components layer, then run
the design lint, contrast checks and the relevant interaction and layout tests.
