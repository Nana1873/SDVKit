# Offline i18n authoring check

`sdvkit project check <mod-root>` validates every direct JSON file in the
selected root's `i18n` directory. `default.json` is the reference locale; each
other top-level locale file is compared with it. Nested locale folders are not
discovered by this check.

The check uses the token grammar implemented by SMAPI 4.5.2:
`{{([ \w\.\-]+)}}`. Token names are trimmed, compared case-insensitively,
and compared as a distinct set, so repeated or reordered uses are compatible.
Text which does not match that grammar remains literal; this check does not
interpret arbitrary Content Patcher expressions or invent an escape syntax.

Missing locale keys and extra locale keys are reported as warnings. Missing
keys are valid because SMAPI falls back from a specific locale to broader
locales and then `default.json`; warnings therefore do not change the check's
exit status. Placeholder name mismatches are errors because they can leave
unsubstituted tokens at runtime. Diagnostics identify the locale file and JSON
key. Case-insensitively duplicated keys, malformed JSON, and schema violations
remain blocking errors as a conservative authoring validation. SMAPI's
runtime loader removes later case-insensitive duplicates with a warning; the
offline check fails instead so the source author must resolve the duplicate
deterministically.

This is a static authoring check only. It does not extract translation keys
from C# code, inspect nested i18n layouts, prove the selected language in-game,
or verify rendered text and runtime fallback. The source files are read-only.
