#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: a SMAPI mod adds a Stone offer to Pierre's shop; buy one and prove the money, stock and inventory changes.

.DESCRIPTION
Builds docs/examples/shop-recipe/ShopPurchaseProof, packages it once and extracts the ZIP into two ready copies.
The broken copy has a misspelled shop ID, which must surface as a runtime diagnostic. The fixed copy must show the
offer (10 Gold, stock 3); one click buys a Stone (money -10, stock 2, Stone held), a second click places it in an
empty inventory slot. The script always stops and resets the review, and compares the normal Mods and Stardew data
folders before and after. Guide: docs/test-a-shop-purchase.md.

.EXAMPLE
pwsh scripts/acceptance/shop.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    [switch] $SkipProtectedCheck
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
$context = Initialize-Acceptance -Name 'shop' -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck
$failed = $false
try {
    $modId = 'ExampleAuthor.ShopPurchaseProof'
    $work = Join-Path $context.Evidence 'work'
    $mod = Join-Path $work 'ShopPurchaseProof'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\examples\shop-recipe\ShopPurchaseProof') -Destination $mod

    Write-Step 'Check, build and package the mod once.'
    Invoke-SdvKit 'doctor' @('doctor', '--game-path', $context.GamePath, '--json') | Out-Null
    Invoke-SdvKit 'check' @('project', 'check', $mod, '--json') | Out-Null
    Invoke-SdvKit 'build' @('project', 'build', $mod, '--game-path', $context.GamePath, '--json') | Out-Null
    $package = Invoke-SdvKit 'package' @('project', 'package', $mod, '--game-path', $context.GamePath, '--json')
    $zip = (Resolve-Path -LiteralPath (Join-Path $mod $package.Json.archive)).Path

    # SMAPI rewrites config.json as UTF-8 without BOM, CRLF and no trailing newline. Writing those exact bytes
    # keeps the staged files unchanged at startup, so SDVKit does not report reviewStagingOwnershipDrifted.
    $utf8 = [Text.UTF8Encoding]::new($false)
    $ready = @{}
    foreach ($variant in @{ broken = 'SeedShpo'; fixed = 'SeedShop' }.GetEnumerator()) {
        $root = Join-Path $work "ready-$($variant.Key)"
        Expand-Archive -LiteralPath $zip -DestinationPath $root
        $ready[$variant.Key] = Join-Path $root 'ShopPurchaseProof'
        [IO.File]::WriteAllText((Join-Path $ready[$variant.Key] 'config.json'), "{`r`n  `"ShopId`": `"$($variant.Value)`"`r`n}", $utf8)
    }

    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null

    Write-Step 'Broken config: the shop must not open, and the mod must report why.'
    Invoke-SdvKit 'start-broken' @('project', 'review', 'start', $ready.broken, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--json') | Out-Null
    Wait-ReviewReady -TestSave | Out-Null
    $command = Invoke-SdvKit 'command-broken' @('project', 'review', 'command', 'shop_purchase_proof_open', '--json')
    Assert-That ($command.Json.commandWritten -eq $true) 'the open command did not run.'
    Start-Sleep -Seconds 2
    $diagnostics = Invoke-SdvKit 'diagnostics-broken' @('project', 'review', 'diagnostics', '--mod', $modId, '--limit', '20', '--json')
    $expected = "Shop Purchase Proof could not open configured shop 'SeedShpo': Data/Shops has no matching entry."
    Assert-That (@($diagnostics.Json.diagnostics | Where-Object { $_.lines -contains $expected }).Count -eq 1) "the diagnostics do not contain: $expected"
    $menu = Invoke-SdvKit 'menu-broken' @('project', 'review', 'menu', '--json')
    Assert-That ($menu.Json.menuOpen -eq $false) 'a menu opened although the config is broken.'
    Stop-Review -Reset -Label 'broken'

    Write-Step 'Fixed config: open the shop and read the offer.'
    Invoke-SdvKit 'start-fixed' @('project', 'review', 'start', $ready.fixed, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--json') | Out-Null
    Wait-ReviewReady -TestSave | Out-Null
    $command = Invoke-SdvKit 'command-fixed' @('project', 'review', 'command', 'shop_purchase_proof_open', '--json')
    Assert-That ($command.Json.commandWritten -eq $true) 'the open command did not run.'
    Start-Sleep -Seconds 2
    $before = (Invoke-SdvKit 'shop-before' @('project', 'review', 'shop', '--json')).Json
    $offer = $before.data.offers[0]
    Assert-That ($before.state -ceq 'ready' -and $before.data.shopId -ceq 'SeedShop' -and $before.data.currency -ceq 'Gold') 'the SeedShop Gold shop is not open.'
    Assert-That ($offer.item.qualifiedItemId -ceq '(O)390' -and $offer.price -eq 10 -and $offer.stock -eq 3 -and $offer.availability -ceq 'available') "offer 0 is not the Stone offer at 10 Gold with stock 3: $($offer | ConvertTo-Json -Compress -Depth 5)"
    Assert-That ($null -eq $before.data.heldItem) 'an item is already held.'
    Assert-That (@($before.data.inventory | Where-Object { $null -eq $_.item }).Count -ge 1) 'the inventory has no empty slot for the Stone.'
    $stonesBefore = (@($before.data.inventory | Where-Object { $_.item.qualifiedItemId -ceq '(O)390' } | ForEach-Object { $_.item.stack }) | Measure-Object -Sum).Sum

    Write-Step 'Buy one Stone: click the top sale row.'
    $menu = (Invoke-SdvKit 'menu-before' @('project', 'review', 'menu', '--json')).Json
    $shopMenu = @($menu.menus | Where-Object adapter -ceq 'shopMenu')
    Assert-That ($shopMenu.Count -eq 1 -and $shopMenu[0].scrollIndex -eq 0) 'expected one unscrolled shop menu.'
    $saleRow = @($shopMenu[0].components | Where-Object { $_.kind -ceq 'saleRow' -and $_.visibleFlag -and $_.intersectsViewport } | Sort-Object { $_.bounds.y })[0]
    Save-ViewportScreenshot 'before-purchase'
    Invoke-ComponentClick 'click-buy' -Menu $menu -Component $saleRow | Out-Null

    $after = (Invoke-SdvKit 'shop-after-purchase' @('project', 'review', 'shop', '--json')).Json
    Assert-That ($after.launchId -ceq $before.launchId -and $after.data.playerId -ceq $before.data.playerId) 'the shop capture belongs to another launch or player.'
    Assert-That ($after.data.money -eq $before.data.money - 10) "money changed from $($before.data.money) to $($after.data.money), expected -10."
    Assert-That ($after.data.offers[0].stock -eq 2) "stock is $($after.data.offers[0].stock), expected 2."
    Assert-That ($after.data.heldItem.qualifiedItemId -ceq '(O)390' -and $after.data.heldItem.stack -eq 1) 'the bought Stone is not held.'

    Write-Step 'Place the held Stone into an empty inventory slot.'
    $menu = (Invoke-SdvKit 'menu-held' @('project', 'review', 'menu', '--json')).Json
    $emptySlot = @($after.data.inventory | Where-Object { $null -eq $_.item })[0].slot
    $slots = @($menu.menus.components | Where-Object { $_.kind -ceq 'inventorySlot' -and $_.controllerId -eq $emptySlot -and $_.visibleFlag -and $_.intersectsViewport })
    Assert-That ($slots.Count -eq 1) "inventory slot $emptySlot has no single visible component."
    Invoke-ComponentClick 'click-place' -Menu $menu -Component $slots[0] | Out-Null

    $final = (Invoke-SdvKit 'shop-final' @('project', 'review', 'shop', '--json')).Json
    $stonesAfter = (@($final.data.inventory | Where-Object { $_.item.qualifiedItemId -ceq '(O)390' } | ForEach-Object { $_.item.stack }) | Measure-Object -Sum).Sum
    Assert-That ($final.data.money -eq $before.data.money - 10 -and $final.data.offers[0].stock -eq 2) 'money or stock changed after placing the Stone.'
    Assert-That ($null -eq $final.data.heldItem -and $stonesAfter -eq $stonesBefore + 1) "the inventory holds $stonesAfter Stones, expected $($stonesBefore + 1), with nothing held."
    Save-ViewportScreenshot 'after-placement'
    Write-Step "Bought one Stone: money $($before.data.money) -> $($final.data.money), stock 3 -> 2, Stones $stonesBefore -> $stonesAfter."

    Stop-Review -Reset -Label 'fixed'
}
catch {
    $failed = $true
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Complete-Acceptance -Failed:$failed
}

if ($failed) { exit 1 }
