# Bundled SMAPI schemas

These three JSON files are unmodified copies from
[Pathoschild/SMAPI](https://github.com/Pathoschild/SMAPI/tree/79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0/src/SMAPI.Web/wwwroot/schemas)
at commit `79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0` (retrieved 2026-09-05).
SMAPI is copyright Pathoschild and contributors; its LGPL-3.0 license is included
as `LICENSE.txt`, with the incorporated GPL-3.0 text in `COPYING.txt`.
The schemas remain separate, replaceable source files in the portable `schemas/`
directory. SDVKit's own source is licensed separately.

| File | Supported authoring scope | SHA-256 of upstream bytes |
| --- | --- | --- |
| `manifest.json` | SMAPI manifest, including `%ProjectVersion%` | `07ae602f4c9e76df1ca38300002438d27eee7100ffa0d25d97386f2198fcd034` |
| `content-patcher.json` | Content Patcher Format 2.9.x | `e8228d81c13e0b8721ea16a8885cfda9b59406ce3fdf492e6430d0f32fc41995` |
| `i18n.json` | One flat translation object with string values | `fc2891224a73612cebdf62e1d27e4058e909c2d82b660ad0766361917d394686` |

## Validation and updates

All three declare JSON Schema Draft 7. SDVKit uses JsonSchema.Net **7.4.0** with
Draft 7 evaluation and invariant English diagnostics. This version uses the .NET
regex engine (with its ECMAScript option and five-second match timeout), which
accepts the upstream inline `(?i)` flags and atomic groups `(?>...)`. Tests cover
those actual expressions, local `$ref`, `oneOf`, `if`/`then`, and `not`, including
failed assertions for which the library supplies no message. The upstream
`@errorMessages` and other UI annotations are not interpreted; SDVKit reports
validator keywords and JSON Pointer fields. Parsing separately permits comments
and trailing commas; duplicate properties are rejected to avoid ambiguous values.

Ordinary checks never download or upload anything. Schemas are loaded only from
the installed `schemas/` directory, and the validator's external fetch callback
is disabled. An authoring `$schema` is accepted only when it exactly matches the
corresponding `https://smapi.io/schemas/<name>.json` URL. Other declarations and
CP format versions are controlled failures, not silently validated as another
format. This is a dated schema snapshot, not a promise of compatibility with all
past or future SMAPI/CP versions. It retains upstream restrictions and omissions;
schema success is not general path safety, reference existence, or game validation.

Updates are deliberate repository changes: select an upstream commit, review all
three files and license, record hashes/provenance, review validator compatibility,
adjust the supported CP format and generator only if needed, run the focused and
complete tests, and verify create/check from a fresh portable extraction. There
is no automatic update command or network fallback. Keep the resource directory
intact when moving or installing SDVKit; missing/unreadable schemas fail closed.
