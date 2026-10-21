#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: a multilingual SMAPI dialogue through the offline i18n check, an English UI, the native title-screen language change, German texts with default-language fallback, persistence and restoring the initial language.

.DESCRIPTION
Uses docs/examples/multilingual-recipe/BlueberryGreeting (English default, German with one deliberately missing key). The
script:

1. breaks one German placeholder and requires `project check` to report exactly the placeholderMismatch problem and the
   missingLocaleKey warning (exit 3), restores it and requires a pass with only the missing-key warning, then builds,
   packages and compares the four expected package entries;
2. starts the extracted package on the test save and requires the English start (empty runtime locale, languageCode en in
   the isolated startup_preferences), opens the question dialogue, requires the English prompt with the farmer name,
   marker and default-only fallback and the response order Cancel, Accept, cancels with a revision-bound mouse click, then
   accepts with another and requires the callback, the unsaved-state guard (saveRequired), a fixture save, and the saved state after a
   stop without reset and a restart;
3. returns to the title screen with the mod's command, selects Language and Deutsch by clicking at positions measured for
   1280x720, requires languageCode de and a 1280x720 title screenshot, loads the fixture from the native Load menu,
   requires the review to be revalidated (status passed with the identity verified), the German runtime locale and the
   accepted state restored from the saved game, reads the German prompt and labels with the English fallback sentence from
   the open dialogue and accepts once more in German (before=true, after=true, changed=false);
4. returns to the title screen again in the same review, selects English, loads the fixture again and requires a second
   revalidation, languageCode en, the original empty locale, accepted=true with pendingSave=false, the English completed
   prompt and an idempotent accept;
5. returns to the title screen, selects Deutsch, stops with reset and requires the reset to put languageCode en back.

The title screen exposes no menu components, so Language, Deutsch, Load and the save row are clicked at fixed positions
measured for the default 1280x720 viewport (each screenshot is checked for that size); pass other positions as parameters.
The fixture must be the only save in the isolated profile, so the Load menu has one row; a lab whose fixture was created
by SDVKit 2.1.0 or earlier also holds a stale `SDVKit2_<id>` copy that the Load menu shows in arbitrary order (issue
#341, see docs/live-review.md). The game's log must name the fixture's save as the loaded one, or the run fails.
Controller input is not covered (issue #271).

Guide: docs/test-a-multilingual-ui.md.

.EXAMPLE
pwsh scripts/acceptance/multilingual-ui.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    [switch] $SkipProtectedCheck,
    # Title-screen positions for a 1280x720 viewport: the Language button and the ENGLISH and Deutsch cells.
    [int] $LanguageX = 1206, [int] $LanguageY = 558,
    [int] $EnglishX = 270, [int] $EnglishY = 222,
    [int] $GermanX = 265, [int] $GermanY = 305,
    # The LOAD button and the first Load-menu row.
    [int] $LoadX = 515, [int] $LoadY = 600,
    [int] $LoadRowX = 640, [int] $LoadRowY = 120
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
$context = Initialize-Acceptance -Name 'multilingual-ui' -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck

$modId = 'SDVKit.Example.BlueberryGreeting'
$profileRoot = Join-Path $context.Lab '.sdvkit\lab\profiles\single\AppData\Roaming\StardewValley'
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)

function Get-LanguageCode {
    $text = Get-Content -Raw -LiteralPath (Join-Path $profileRoot 'startup_preferences')
    if ($text -match '<languageCode>([^<]*)</languageCode>') { return $Matches[1] }
    return ''
}

function Get-Greeting([string] $Label, [string] $State, [string] $Command = 'blueberry-greeting status') {
    # The mod's own log line for the command, as an object: state, locale, accepted, pendingSave, ...
    $match = Invoke-ModLogCommand $Label -Command $Command -Pattern "BLUEBERRY_GREETING (\{`"state`":`"$State`"[^\r\n]*\})" -Log $script:smapiLog
    return ($match[1] | ConvertFrom-Json)
}

function Get-Menu([string] $Label) {
    (Invoke-SdvKit "menu-$Label" @('project', 'review', 'menu', '--json')).Json
}

function Get-StableMenu([string] $Label) {
    # A dialogue types its text out; wait until two reads agree.
    $previous = $null
    foreach ($attempt in 1..10) {
        Start-Sleep -Seconds 1
        $menu = Get-Menu "$Label-$attempt"
        if ($menu.menuOpen -eq $true -and $menu.uiRevision -ceq $previous) { return $menu }
        $previous = $menu.uiRevision
    }
    throw "Acceptance failed: the menu '$Label' did not settle."
}

function Invoke-MenuClick([string] $Label, $Bounds) {
    # The click moves the virtual cursor first, and a hover changes the menu revision. Put the cursor on the target, read a
    # fresh revision and click the same point, so the revision-bound click is not interrupted by its own cursor move.
    $x = [int]$Bounds.x + [int][Math]::Floor($Bounds.width / 2)
    $y = [int]$Bounds.y + [int][Math]::Floor($Bounds.height / 2)
    Invoke-Tool "cursor-$Label" 'stardew_input_cursor_set' @{ x = $x; y = $y } -OptIn '--allow-input' | Out-Null
    Start-Sleep -Seconds 1
    $menu = Get-Menu "fresh-$Label"
    $click = Invoke-Tool "click-$Label" 'stardew_input_click' @{ x = $x; y = $y; button = 'MouseLeft'; count = 1; uiRevision = [string]$menu.uiRevision } -OptIn '--allow-input'
    Assert-That ($click.Json.succeeded -eq $true -and $click.Json.released -eq $true) "the click '$Label' did not complete ($($click.Json.problem.code)). Inspect the game state before any repeat."
    Start-Sleep -Milliseconds 1500
}

function Close-Dialogue([string] $Label) {
    foreach ($attempt in 1..6) {
        $menu = Get-Menu "$Label-$attempt"
        if ($menu.menuOpen -eq $false) { return }
        $box = @($menu.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.DialogueBox' })
        Assert-That ($box.Count -eq 1) "expected a dialogue, found $(@($menu.menus | ForEach-Object type) -join ', ')."
        Invoke-MenuClick "$Label-$attempt" $box[0].bounds
    }
    Assert-That ((Get-Menu "$Label-closed").menuOpen -eq $false) 'the dialogue did not close.'
}

function Open-Greeting([string] $Label) {
    Get-Greeting "open-$Label" 'dialogueOpened' 'blueberry-greeting open' | Out-Null
    $menu = Get-StableMenu "dialogue-$Label"
    $box = @($menu.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.DialogueBox' })
    Assert-That ($box.Count -eq 1 -and $null -ne $box[0].dialogue) "the greeting opened $(@($menu.menus | ForEach-Object type) -join ', '), expected one dialogue."
    $keys = @($box[0].dialogue.choices | ForEach-Object key)
    Assert-That (($keys -join ',') -ceq 'blueberry_cancel,blueberry_accept') "the responses are $($keys -join ', '), expected blueberry_cancel then blueberry_accept."
    return $box[0].dialogue
}

function Answer-Greeting([string] $Label, $Dialogue, [string] $Key) {
    $choice = @($Dialogue.choices | Where-Object key -ceq $Key)[0]
    Invoke-MenuClick $Label $choice.bounds
}

function Assert-Answer([string] $Label, [string] $Key, [bool] $Before, [bool] $After, [bool] $Changed) {
    # The answer callback logs once per answer; read the newest one from the log tail.
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 500
        $stream = [IO.File]::Open($script:smapiLog, 'Open', 'Read', 'ReadWrite')
        try { $text = [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
        $answers = @([regex]::Matches($text, 'BLUEBERRY_GREETING (\{"state":"answered"[^\r\n]*\})') | ForEach-Object { $_.Groups[1].Value | ConvertFrom-Json })
    } until (($answers.Count -gt $script:answerCount) -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($answers.Count -gt $script:answerCount) "no new answer was logged for '$Label'."
    $answer = $answers[-1]
    $script:answerCount = $answers.Count
    Assert-That ($answer.answer -ceq $Key -and $answer.before -eq $Before -and $answer.after -eq $After -and $answer.changed -eq $Changed) "the '$Label' callback logged answer=$($answer.answer) before=$($answer.before) after=$($answer.after) changed=$($answer.changed)."
}

function Save-TitleScreenshot([string] $Label) {
    # Captures the viewport with the console line the title screen still accepts and keeps the PNG; it must be 1280x720.
    $name = "$Label-$runId"
    Invoke-SdvKit "screenshot-$Label" @('project', 'review', 'command', "sdvkit screenshot viewport $name", '--json') | Out-Null
    $file = Join-Path $profileRoot "Screenshots\SDVKit-$name.png"
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $file -PathType Leaf) -and [DateTime]::UtcNow -le $deadline) { Start-Sleep -Milliseconds 500 }
    Assert-That (Test-Path -LiteralPath $file -PathType Leaf) "the title screenshot $Label was not written."
    Start-Sleep -Milliseconds 500
    $header = [byte[]]::new(24)
    $stream = [IO.File]::OpenRead($file)
    try { [void]$stream.Read($header, 0, 24) } finally { $stream.Dispose() }
    $width = ([int]$header[16] -shl 24) + ([int]$header[17] -shl 16) + ([int]$header[18] -shl 8) + [int]$header[19]
    $height = ([int]$header[20] -shl 24) + ([int]$header[21] -shl 16) + ([int]$header[22] -shl 8) + [int]$header[23]
    Assert-That ($width -eq 1280 -and $height -eq 720) "the title screenshot is ${width}x$height; the click positions are for 1280x720. Measure them on the screenshot and pass them as parameters."
    Copy-Item -LiteralPath $file -Destination (Join-Path $context.Evidence "$Label.png")
}

function Invoke-TitleClick([string] $Label, [int] $X, [int] $Y) {
    foreach ($line in "sdvkit input cursor $X $Y", 'sdvkit input press MouseLeft') {
        $result = Invoke-SdvKit "title-$Label" @('project', 'review', 'command', $line, '--json')
        Assert-That ($result.Json.commandWritten -eq $true) "the title command '$line' was not written."
        Start-Sleep -Milliseconds 1500
    }
    Start-Sleep -Seconds 2
}

function Select-Language([string] $Label, [int] $X, [int] $Y, [string] $Code) {
    Invoke-TitleClick "$Label-language" $LanguageX $LanguageY
    Save-TitleScreenshot "$Label-grid"
    Invoke-TitleClick "$Label-choice" $X $Y
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Get-LanguageCode) -cne $Code -and [DateTime]::UtcNow -le $deadline) { Start-Sleep -Milliseconds 500 }
    Assert-That ((Get-LanguageCode) -ceq $Code) "the startup preference language is '$(Get-LanguageCode)', expected '$Code'."
    Save-TitleScreenshot "$Label-title"
}

function Assert-OnlyFixtureSave {
    # At the title screen the suspended review makes status exit 3 with only testSaveFailed until the reload.
    $read = Invoke-SdvKit 'status-row' @('project', 'review', 'status', '--topology', 'single', '--json') -AllowExit @(0, 3)
    $status = $read.Json
    Assert-That ($read.Exit -eq 0 -or (@($status.problems).Count -eq 1 -and $status.problems[0].code -ceq 'testSaveFailed')) 'the title-screen status reports a problem other than the suspended fixture.'
    $saveId = [string]$status.lab.gameSide.testSave.saveId
    $script:saveId = $saveId
    # The Load menu orders saves by their save minute, so a second save of the same farm could take the first row; loading it
    # is refused and ends the review. Require the fixture to be the only save: the Load menu then has exactly one row.
    $folders = [string[]]@(Get-ChildItem -LiteralPath (Join-Path $profileRoot 'Saves') -Directory | ForEach-Object Name)
    Assert-That ($folders.Count -eq 1 -and $folders[0] -ceq $saveId) "the isolated profile holds the saves $($folders -join ', '); only the fixture $saveId may be there. A stale SDVKit2_ copy comes from a fixture created by SDVKit 2.1.0 or earlier (#341): stop the lab and remove that folder from $(Join-Path $profileRoot 'Saves'), or use a new lab root."
}

function Load-FixtureFromTitle([string] $Label) {
    # Native Load menu, then its only row; the review then revalidates the registered save.
    Assert-OnlyFixtureSave
    Invoke-TitleClick "$Label-load" $LoadX $LoadY
    Save-TitleScreenshot "$Label-load"
    Invoke-TitleClick "$Label-row" $LoadRowX $LoadRowY
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Seconds 3
        $loaded = Select-String -LiteralPath $script:smapiLog -Pattern 'Context: loaded save' -SimpleMatch -ErrorAction SilentlyContinue
        $count = @($loaded).Count
    } until ($count -gt $script:loadCount -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($count -gt $script:loadCount) 'the fixture row did not load a save.'
    $script:loadCount = $count
    $last = @($loaded)[-1].Line
    Assert-That ($last -clike "*loaded save '$($script:saveId)'*") "the Load menu row loaded a different save: $last"
    Wait-Revalidated $Label
}

function Wait-Revalidated([string] $Label) {
    # The one reload of the registered save after a return to title is checked again before commands work (#341).
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $read = Invoke-SdvKit "status-$Label" @('project', 'review', 'status', '--topology', 'single', '--json') -AllowExit @(0, 3)
        $testSave = $read.Json.lab.gameSide.testSave
    } until (($read.Exit -eq 0 -and $testSave.phase -ceq 'passed') -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($read.Exit -eq 0 -and @($read.Json.problems).Count -eq 0) "after the reload the status reports $(@($read.Json.problems | ForEach-Object code) -join ', '): $($testSave.message)"
    Assert-That ($testSave.phase -ceq 'passed' -and $testSave.identityVerified -eq $true -and $testSave.message -ceq 'Exact fixture reloaded and revalidated for interactive review.') "the reload was not revalidated: phase $($testSave.phase), identityVerified $($testSave.identityVerified), '$($testSave.message)'."
    Write-Step 'Revalidated after the title reload: phase passed, identityVerified true.'
}

function Go-ToTitle([string] $Label) {
    Get-Greeting "title-$Label" 'titleRequested' 'blueberry-greeting title' | Out-Null
    Start-Sleep -Seconds 6
    Save-TitleScreenshot "$Label-initial"
}

function Start-Package([string] $Label) {
    Invoke-SdvKit "start-$Label" @('project', 'review', 'start', $script:ready, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--json') | Out-Null
    $script:smapiLog = Get-SmapiLogPath (Wait-ReviewReady -TestSave)
    $script:answerCount = 0
    $script:loadCount = @(Select-String -LiteralPath $script:smapiLog -Pattern 'Context: loaded save' -SimpleMatch).Count
}

function Save-Fixture([string] $Label) {
    $save = Invoke-Tool "fixture-save-$Label" 'stardew_fixture_save' -OptIn '--allow-fixture-actions'
    Assert-That ($save.Json.state -ceq 'ready' -and $save.Json.mayHaveRun -eq $false) "the fixture save '$Label' did not complete."
}

$failed = $false
try {
    $work = Join-Path $context.Evidence 'work'
    $mod = Join-Path $work 'BlueberryGreeting'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\examples\multilingual-recipe\BlueberryGreeting') -Destination $mod
    Invoke-SdvKit 'doctor' @('doctor', '--game-path', $context.GamePath, '--json') | Out-Null

    Write-Step 'Offline: a deliberate placeholder error, one corrected token, build and package.'
    $deFile = Join-Path $mod 'i18n\de.json'
    $source = [IO.File]::ReadAllText($deFile)
    $broken = $source.Replace('"Hallo {{farmer}}!', '"Hallo {{player}}!')
    Assert-That ($broken -cne $source) 'the sample no longer holds the German prompt to break.'
    [IO.File]::WriteAllText($deFile, $broken, [Text.UTF8Encoding]::new($false))
    $check = Invoke-SdvKit 'check-broken' @('project', 'check', $mod, '--json') -AllowExit @(3)
    Assert-That ($check.Exit -eq 3 -and @($check.Json.problems).Count -eq 1 -and $check.Json.problems[0].code -ceq 'placeholderMismatch' -and $check.Json.problems[0].file -ceq 'i18n/de.json' -and $check.Json.problems[0].field -ceq '/dialogue.prompt') 'the deliberate placeholder error was not reported as the only problem.'
    Assert-That (@($check.Json.warnings).Count -eq 1 -and $check.Json.warnings[0].code -ceq 'missingLocaleKey' -and $check.Json.warnings[0].field -ceq '/fallback.default-only') 'the intentional missing key was not the only warning.'
    [IO.File]::WriteAllText($deFile, $source, [Text.UTF8Encoding]::new($false))
    $fixed = Invoke-SdvKit 'check-fixed' @('project', 'check', $mod, '--json')
    Assert-That ($fixed.Json.status -ceq 'passed' -and @($fixed.Json.problems).Count -eq 0 -and @($fixed.Json.warnings).Count -eq 1 -and $fixed.Json.warnings[0].field -ceq '/fallback.default-only') 'the corrected sample did not pass with exactly the intentional warning.'
    Invoke-SdvKit 'build' @('project', 'build', $mod, '--game-path', $context.GamePath, '--json') | Out-Null
    $package = Invoke-SdvKit 'package' @('project', 'package', $mod, '--game-path', $context.GamePath, '--json')
    $expected = 'BlueberryGreeting/BlueberryGreeting.dll', 'BlueberryGreeting/i18n/de.json', 'BlueberryGreeting/i18n/default.json', 'BlueberryGreeting/manifest.json'
    $entries = (@($package.Json.entries) | Sort-Object -CaseSensitive) -join '|'
    Assert-That ($entries -ceq (($expected | Sort-Object -CaseSensitive) -join '|')) "the package entries are $(@($package.Json.entries) -join ', ')."
    $zip = (Resolve-Path -LiteralPath (Join-Path $mod $package.Json.archive)).Path
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'package')
    $ready = Join-Path $work 'package\BlueberryGreeting'
    Invoke-SdvKit 'check-extracted' @('project', 'check', $ready, '--json') | Out-Null
    foreach ($file in 'manifest.json', 'i18n\de.json', 'i18n\default.json') {
        Assert-That ((Get-FileHash (Join-Path $mod $file)).Hash -eq (Get-FileHash (Join-Path $ready $file)).Hash) "$file differs between the source and the package."
    }
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null

    Write-Step 'English start: the dialogue, then cancel with a mouse click.'
    Start-Package 'initial'
    $initialLanguage = Get-LanguageCode
    Assert-That ($initialLanguage -ceq 'en') "the isolated startup preference is '$initialLanguage', not an English-first run; 'project review reset --topology single' restores English while the lab is stopped."
    $initial = Get-Greeting 'status-initial' 'ready'
    Assert-That ($initial.locale -ceq '' -and $initial.accepted -eq $false -and $initial.pendingSave -eq $false -and $initial.marker -ceq 'BLUEBERRY-42' -and $initial.fallback -ceq 'Default-only marker: BLUEBERRY.') "the initial status is $($initial | ConvertTo-Json -Compress)."
    Save-ViewportScreenshot 'initial-world'
    $dialogue = Open-Greeting 'en'
    Assert-That ($dialogue.text -ceq 'Hello SDVKit! Recipe marker: BLUEBERRY-42. Default-only marker: BLUEBERRY.') "the English prompt is '$($dialogue.text)'."
    Assert-That ($dialogue.choices[0].text -ceq 'Not now' -and $dialogue.choices[1].text -ceq 'Remember this greeting') 'the English response labels are wrong.'
    Save-ViewportScreenshot 'en-open'
    Answer-Greeting 'en-cancel' $dialogue 'blueberry_cancel'
    Assert-Answer 'English cancel' 'blueberry_cancel' $false $false $false
    Close-Dialogue 'en-result'
    $afterCancel = Get-Greeting 'status-cancelled' 'ready'
    Assert-That ($afterCancel.accepted -eq $false -and $afterCancel.pendingSave -eq $false) 'cancelling changed the state.'

    Write-Step 'English accept with a mouse click, then save.'
    $dialogue = Open-Greeting 'en-accept'
    Answer-Greeting 'en-accept' $dialogue 'blueberry_accept'
    Assert-Answer 'English accept' 'blueberry_accept' $false $true $true
    Close-Dialogue 'en-accept-result'
    $accepted = Get-Greeting 'status-accepted' 'ready'
    Assert-That ($accepted.accepted -eq $true -and $accepted.pendingSave -eq $true) 'accepting did not leave accepted=true with a pending save.'
    $guard = Get-Greeting 'title-unsaved' 'saveRequired' 'blueberry-greeting title'
    Assert-That ($guard.pendingSave -eq $true) 'the unsaved-state guard did not refuse the title transition.'
    Assert-That ((Get-Menu 'still-loaded').menuOpen -eq $false) 'a menu is open after the refused transition.'
    Save-Fixture 'accepted'
    # The fixture save does not publish SMAPI's Saved event, so pendingSave stays true in this process; a restart reads the saved state.
    Stop-Review -Label 'after-save'
    Start-Package 'persisted'
    $persisted = Get-Greeting 'status-persisted' 'ready'
    Assert-That ($persisted.accepted -eq $true -and $persisted.pendingSave -eq $false) "after the save and restart the status is $($persisted | ConvertTo-Json -Compress)."

    Write-Step 'Language change through the native title screen: Deutsch, then the fixture from the native Load menu.'
    Go-ToTitle 'to-de'
    Select-Language 'de' $GermanX $GermanY 'de'
    Load-FixtureFromTitle 'de'
    $de = Get-Greeting 'status-de' 'ready'
    Assert-That ($de.locale -like 'de*' -and $de.accepted -eq $true -and $de.pendingSave -eq $false -and $de.fallback -ceq 'Default-only marker: BLUEBERRY.') "the German world reports $($de | ConvertTo-Json -Compress)."
    $dialogue = Open-Greeting 'de'
    Assert-That ($dialogue.text -ceq "Willkommen zur$([char]0xFC)ck, SDVKit. Rezeptmarke: BLUEBERRY-42. Default-only marker: BLUEBERRY.") "the German prompt is '$($dialogue.text)'."
    Assert-That ($dialogue.choices[0].text -ceq 'Jetzt nicht' -and $dialogue.choices[1].text -ceq "Diesen Gru$([char]0xDF) merken") "the German response labels are '$($dialogue.choices[0].text)', '$($dialogue.choices[1].text)'."
    Save-ViewportScreenshot 'de-open'
    Answer-Greeting 'de-accept' $dialogue 'blueberry_accept'
    Assert-Answer 'German accept' 'blueberry_accept' $true $true $false
    Close-Dialogue 'de-result'
    Write-Step "German runtime locale '$($de.locale)': translated prompt and labels with the default-language fallback sentence read from the open dialogue; the accepted state came back from the saved game."

    Write-Step 'Second return to title in the same review: English, then the fixture from the native Load menu again.'
    Go-ToTitle 'to-en'
    Select-Language 'en' $EnglishX $EnglishY 'en'
    Load-FixtureFromTitle 'en'
    $restored = Get-Greeting 'status-en' 'ready'
    Assert-That ($restored.locale -ceq '' -and $restored.accepted -eq $true -and $restored.pendingSave -eq $false) "after the English reload the status is $($restored | ConvertTo-Json -Compress)."
    Assert-That ((Get-LanguageCode) -ceq $initialLanguage) 'the startup preference language was not restored.'
    $dialogue = Open-Greeting 'en-again'
    Assert-That ($dialogue.text -ceq 'Welcome back, SDVKit. Recipe marker: BLUEBERRY-42. Default-only marker: BLUEBERRY.') "the English completed prompt is '$($dialogue.text)'."
    Save-ViewportScreenshot 'reload-en'
    Answer-Greeting 'en-again-accept' $dialogue 'blueberry_accept'
    Assert-Answer 'repeated accept' 'blueberry_accept' $true $true $false
    Close-Dialogue 'en-again-result'
    Invoke-SdvKit 'diagnostics' @('project', 'review', 'diagnostics', '--mod', $modId, '--limit', '30', '--json') | Out-Null

    Write-Step 'Reset after a language change: Deutsch on the title screen, then stop with reset.'
    Go-ToTitle 'to-de-reset'
    Select-Language 'de-reset' $GermanX $GermanY 'de'
    Stop-Review -Reset -Label 'final'
    Assert-That ((Get-LanguageCode) -ceq 'en') "the reset left languageCode '$(Get-LanguageCode)' in the isolated startup preferences (#340)."
    Write-Step 'The reset restored languageCode en.'
}
catch {
    $failed = $true
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Complete-Acceptance -Failed:$failed
}

if ($failed) { exit 1 }
