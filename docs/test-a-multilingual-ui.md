# Test a multilingual mod dialogue and the title-screen language change

Use this pattern when your mod ships translations and you want to prove the offline i18n check, the English and German text in the running game, and that the state survives a language change and a restart. The complete worked example is [`scripts/acceptance/multilingual-ui.ps1`](../scripts/acceptance/multilingual-ui.ps1) with the mod in [`docs/examples/multilingual-recipe/BlueberryGreeting`](examples/multilingual-recipe/BlueberryGreeting/): a console command opens a question dialogue with Cancel first and Accept second; accepting once writes one player `modData` value, accepting again changes nothing. English is the default locale, German the second, and one German key (`fallback.default-only`) is deliberately missing so SMAPI's fallback to `default.json` can be seen.

Read [offline i18n authoring check](i18n-authoring.md), [live review](live-review.md) and [menu inspection](menu-inspection.md) first. The script takes several minutes and starts the game twice; the language changes happen inside one review through the [return to title](live-review.md#finish-or-test-persistence) and its revalidated reload. After `lab test-save` the script requires that the stale `SDVKit2_<uniqueGameId>` copy of an older lab is gone (see [live review](live-review.md#stale-fixture-copy)), so the Load menu's first row is the fixture.

| Gate | Proves | Does not prove |
| --- | --- | --- |
| `project check` | JSON validity, the same keys in every locale, compatible placeholder names | The selected in-game language, rendered text or runtime fallback |
| `project build` and `project package` | The project compiles; the ZIP holds exactly the four expected entries | That the DLL behaves in the game |
| Live review of the extracted package | Text, input, language, state and persistence at the tested scope | Other worlds, languages or devices |

## 1. Offline: one deliberate error, one corrected token

Break one German placeholder (`{{player}}` where the source supplies `farmer`) and require `project check` to exit `3` with exactly one `placeholderMismatch` problem for `i18n/de.json` `/dialogue.prompt`, plus the intended `missingLocaleKey` warning. Restore the token: the check must pass with only that warning. Then build, package, require the four entries (DLL, `manifest.json` and both locale files) and compare the extracted copy with the source byte for byte.

## 2. English first, in the running game

The isolated profile's `startup_preferences` must say `languageCode` `en`; `project review reset` restores that while the lab is stopped. Read the mod's log line: empty runtime locale (SMAPI's default locale), not accepted, marker `BLUEBERRY-42`. Open the dialogue and read `stardew_menu_get`: the prompt names the farmer, the marker and `Default-only marker: BLUEBERRY.`, and the responses are `blueberry_cancel` then `blueberry_accept`. A click on a dialogue choice moves the virtual cursor first, and a hover changes the menu revision, so put the cursor on the choice with `stardew_input_cursor_set`, read the menu again, and click the same point with that revision. Cancel once and require `before=false, after=false, changed=false`; accept once and require `before=false, after=true, changed=true`, then `pendingSave=true`; the title command must refuse with `saveRequired`. Close each result dialogue by clicking it (`Escape` does not close it).

## 3. Save, restart, and the title-screen language change

`stardew_fixture_save` does not publish SMAPI's `Saved` event, so `pendingSave` stays `true` in that process: stop **without** reset and start the same package again, and require `accepted=true, pendingSave=false`. Then deliver the mod's `title` command and use the native title screen: click the Language button and the Deutsch cell with `sdvkit input cursor` and `sdvkit input press MouseLeft` lines (`project review command`; the input tools need a loaded fixture), and require `languageCode` `de` in the profile's `startup_preferences` and 1280x720 screenshots (kept as evidence; look at them: the title reads NEU, LADEN, KOOP, VERLASSEN). The title screen exposes no menu components, so the click positions are measured for the default 1280x720 viewport and can be passed as parameters.

Load the fixture from the native Load menu's first row; the game's log must name the fixture's save as the loaded one. The review then revalidates the reload: `project review status` must report no problems, phase `passed`, `identityVerified=true` and `Exact fixture reloaded and revalidated for interactive review.`. Read the mod's status line: the German runtime locale (`de-de`) and `accepted=true`, so the state came back from the saved game. Open the dialogue and read `stardew_menu_get`: the German completed prompt and labels with the English fallback sentence. Accept once more in German and require `before=true, after=true, changed=false`.

## 4. Back to English in the same review, and the reset

Deliver `title` again, click Language and ENGLISH and load the fixture from the Load menu a second time: each return to title from a resumed review allows one reload. Require the second revalidation, `languageCode` `en`, the empty runtime locale, `accepted=true, pendingSave=false`, the English completed prompt, and another Accept with `before=true, after=true, changed=false`.

Finally deliver `title`, select Deutsch, and stop with reset at the title screen. The reset must leave `languageCode` `en` in the isolated `startup_preferences`, so the next run starts in English.

## Not covered

- **Controller input** (D-pad focus and `ControllerA` on the dialogue) is deferred under [#271](https://github.com/Nana1873/SDVKit/issues/271); the script accepts and cancels by mouse.
- Other locales and languages with different fonts, and a normal save or the language of a normal profile.
