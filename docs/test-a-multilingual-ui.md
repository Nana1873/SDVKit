# Test a multilingual mod dialogue and the title-screen language change

Use this pattern when your mod ships translations and you want to prove the offline i18n check, the English and German text in the running game, and that the state survives a language change and a restart. The complete worked example is [`scripts/acceptance/multilingual-ui.ps1`](../scripts/acceptance/multilingual-ui.ps1) with the mod in [`docs/examples/multilingual-recipe/BlueberryGreeting`](examples/multilingual-recipe/BlueberryGreeting/): a console command opens a question dialogue with Cancel first and Accept second; accepting once writes one player `modData` value, accepting again changes nothing. English is the default locale, German the second, and one German key (`fallback.default-only`) is deliberately missing so SMAPI's fallback to `default.json` can be seen.

Read [offline i18n authoring check](i18n-authoring.md), [live review](live-review.md) and [menu inspection](menu-inspection.md) first. The script takes several minutes and restarts the game five times.

| Gate | Proves | Does not prove |
| --- | --- | --- |
| `project check` | JSON validity, the same keys in every locale, compatible placeholder names | The selected in-game language, rendered text or runtime fallback |
| `project build` and `project package` | The project compiles; the ZIP holds exactly the four expected entries | That the DLL behaves in the game |
| Live review of the extracted package | Text, input, language, state and persistence at the tested scope | Other worlds, languages or devices |

## 1. Offline: one deliberate error, one corrected token

Break one German placeholder (`{{player}}` where the source supplies `farmer`) and require `project check` to exit `3` with exactly one `placeholderMismatch` problem for `i18n/de.json` `/dialogue.prompt`, plus the intended `missingLocaleKey` warning. Restore the token: the check must pass with only that warning. Then build, package, require the four entries (DLL, `manifest.json` and both locale files) and compare the extracted copy with the source byte for byte.

## 2. English first, in the running game

The isolated profile's `startup_preferences` must say `languageCode` `en` (the script restores that itself if an earlier failed run left German there). Read the mod's log line: empty runtime locale (SMAPI's default locale), not accepted, marker `BLUEBERRY-42`. Open the dialogue and read `stardew_menu_get`: the prompt names the farmer, the marker and `Default-only marker: BLUEBERRY.`, and the responses are `blueberry_cancel` then `blueberry_accept`. A click on a dialogue choice moves the virtual cursor first, and a hover changes the menu revision, so put the cursor on the choice with `stardew_input_cursor_set`, read the menu again, and click the same point with that revision. Cancel once and require `before=false, after=false, changed=false`; accept once and require `before=false, after=true, changed=true`, then `pendingSave=true`; the title command must refuse with `saveRequired`. Close each result dialogue by clicking it (`Escape` does not close it).

## 3. Save, restart, and the title-screen language change

`stardew_fixture_save` does not publish SMAPI's `Saved` event, so `pendingSave` stays `true` in that process: stop **without** reset and start the same package again, and require `accepted=true, pendingSave=false`. Then deliver the mod's `title` command and use the native title screen: click the Language button and the Deutsch cell with `sdvkit input cursor` and `sdvkit input press MouseLeft` lines (`project review command`; the input tools need a loaded fixture), and require `languageCode` `de` in the profile's `startup_preferences` and 1280x720 screenshots (kept as evidence; look at them: the title reads NEU, LADEN, KOOP, VERLASSEN). The title screen exposes no menu components, so the click positions are measured for the default 1280x720 viewport and can be passed as parameters.

Load the fixture from the native Load menu. It lists two identical-looking saves, the work save and an `SDVKit2_` copy of it, newest first; the script picks the row by the write time of each save's `SaveGameInfo` and requires the game's log to name the fixture's save as the loaded one. The mod's own log line written when the save loads must then show the German runtime locale (`de-de`), the German prompt and labels with the English fallback sentence, and `accepted=true`: the state came back from the saved game.

**Finding, not worked around:** the review does not revalidate the fixture after a title-screen reload. Status stays `testSaveFailed` ("The live world differs in these exact fixture fields...") and every command, including mod console commands, tool and menu read, answers `reviewTestSaveNotReady`/blocked. That is why the German world is read from the mod's log line at save load and not from a menu read, and why accepting in German is not part of the script. The older recipe describes a working revalidation; this needs a separate fix in the game-side test-save automation.

## 4. Restore English and prove idempotence

The review has to be stopped (no reset) and started again: the autoload begins in the empty locale although the German preference is set, so deliver `title`, click Language and ENGLISH, stop without reset and start the same package once more. Require `languageCode` `en`, the original empty locale, `accepted=true, pendingSave=false`, the English completed prompt, and a second Accept with `before=true, after=true, changed=false`. Finish with stop and reset.

## Not covered

- **Controller input** (D-pad focus and `ControllerA` on the dialogue) is deferred under [#271](https://github.com/Nana1873/SDVKit/issues/271); the script accepts and cancels by mouse.
- A rendered German dialogue (menu read or screenshot of the open dialogue) and a German accept, because of the revalidation finding above.
- Other locales and languages with different fonts, and a normal save or the language of a normal profile.
