param(
    [Parameter(Mandatory=$true)][string]$SeaPowerDir,
    [Parameter(Mandatory=$true)][string]$AnchorChainDir,
    [Parameter(Mandatory=$true)][string]$OriginalModDir,
    [Parameter(Mandatory=$true)][string]$PluginPath,
    [Parameter(Mandatory=$true)][string]$CatalogPath,
    [Parameter(Mandatory=$true)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$frameworkFacade = [Reflection.Assembly]::Load('netstandard, Version=2.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51')
$folders = @((Join-Path $SeaPowerDir 'BepInEx\core'), (Join-Path $SeaPowerDir 'Sea Power_Data\Managed'), $AnchorChainDir, (Split-Path $PluginPath))
[AppDomain]::CurrentDomain.add_AssemblyResolve([ResolveEventHandler]{param($sender, $eventArgs)
    $requested = ([Reflection.AssemblyName]::new($eventArgs.Name)).Name
    if ($requested -eq 'netstandard') { return $frameworkFacade }
    $existing = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $requested })
    if ($existing.Count -gt 0) { return $existing[0] }
    if ($requested -eq 'mscorlib' -or $requested.StartsWith('System.')) { return $null }
    foreach ($folder in $folders) {
        $candidate = Join-Path $folder ($requested + '.dll')
        if (Test-Path $candidate) { return [Reflection.Assembly]::LoadFrom($candidate) }
    }
    return $null
})
$report = [ordered]@{ passed = $false; gameRenderingTested = $false }
try {
    [Reflection.Assembly]::LoadFrom((Join-Path $SeaPowerDir 'BepInEx\core\0Harmony.dll')) | Out-Null
    [Reflection.Assembly]::LoadFrom($PluginPath) | Out-Null
    $original = [Reflection.Assembly]::LoadFrom((Join-Path $OriginalModDir 'DynamicCampaignMod.dll'))
    $flags = [Reflection.BindingFlags]'Instance,Static,NonPublic,Public'
    $fleet = $original.GetType('DynamicCampaignMod.UI.Strategy.FleetViewModel', $true)
    $refresh = $fleet.GetMethod('RefreshShips', $flags)
    $catalog = [DynamicCampaignJapanese.TranslationCatalog]::Load($CatalogPath)
    $integration = [DynamicCampaignJapanese.OriginalModIntegration]::new($catalog)
    function New-CacheFixture {
        $state = [Activator]::CreateInstance($original.GetType('DynamicCampaignMod.Core.State.CampaignState', $true))
        $forces = $state.GetType().GetField('TaskForces', $flags).GetValue($state)
        $forceType = $original.GetType('DynamicCampaignMod.Core.State.TaskForce', $true)
        for ($index = 1; $index -le 2; $index++) {
            $force = [Activator]::CreateInstance($forceType)
            $forceType.GetField('Id', $flags).SetValue($force, $index)
            $forceType.GetField('Name', $flags).SetValue($force, ('第' + $index + '艦隊'))
            $forceType.GetField('SideId', $flags).SetValue($force, 'Blue')
            $forces.Add($force)
        }
        $simType = $original.GetType('DynamicCampaignMod.Core.Sim.CampaignSim', $true)
        $sim = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($simType)
        $simType.GetField('State', $flags).SetValue($sim, $state)
        $view = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($fleet)
        $fleet.GetField('_sim', $flags).SetValue($view, $sim)
        $fleet.GetField('_sideId', $flags).SetValue($view, 'Blue')
        $groups = $fleet.GetField('<Groups>k__BackingField', $flags)
        $groups.SetValue($view, [Activator]::CreateInstance($groups.FieldType))
        return $view
    }
    function TaskForce-Snapshot($state) {
        return ($state.GetType().GetField('TaskForces', $flags).GetValue($state) | ForEach-Object {
            $force = $_
            @('Id', 'Name', 'SideId', 'PortId') | ForEach-Object { $force.GetType().GetField($_, $flags).GetValue($force) }
            $force.GetType().GetField('Ships', $flags).GetValue($force).Count
        }) | ConvertTo-Json -Compress
    }
    function Test-CacheFixture {
        $view = New-CacheFixture
        $refresh.Invoke($view, @()) | Out-Null
        $groups = $fleet.GetProperty('Groups', $flags).GetValue($view)
        $rows = @($groups | ForEach-Object { $_ })
        $before = $fleet.GetField('_signature', $flags).GetValue($view)
        $sim = $fleet.GetField('_sim', $flags).GetValue($view)
        $state = $sim.GetType().GetField('State', $flags).GetValue($sim)
        $stateBefore = TaskForce-Snapshot $state
        $preserved = $true
        for ($iteration = 0; $iteration -lt 10; $iteration++) {
            $refresh.Invoke($view, @()) | Out-Null
            for ($row = 0; $row -lt $rows.Count; $row++) {
                if (-not [object]::ReferenceEquals($rows[$row], $groups[$row])) { $preserved = $false }
            }
        }
        $after = $fleet.GetField('_signature', $flags).GetValue($view)
        $stateUnchanged = $stateBefore -eq (TaskForce-Snapshot $state)
        $summary = $fleet.GetProperty('Summary', $flags).GetValue($view)
        $force = $state.GetType().GetField('TaskForces', $flags).GetValue($state)[0]
        $force.GetType().GetField('Name', $flags).SetValue($force, '第1艦隊（変更）')
        $beforeChanged = $groups[0]
        $refresh.Invoke($view, @()) | Out-Null
        return [pscustomobject]@{
            rows = $groups.Count; rowPreserved = $preserved; unchangedRefreshes = 10
            signatureEqual = [string]::Equals($before, $after, [StringComparison]::Ordinal)
            taskForceStateUnchanged = $stateUnchanged; summary = $summary
            rebuiltWhenDataChanges = -not [object]::ReferenceEquals($beforeChanged, $groups[0])
        }
    }
    $report.original = Test-CacheFixture
    $owner = [HarmonyLib.Harmony]::new('DynamicCampaignJapanese.Verification.Runtime')
    # Exercise the real patch callback without loading unrelated game APIs into .NET Framework.
    [DynamicCampaignJapanese.OriginalModIntegration].GetField('current', $flags).SetValue($null, $integration)
    $patch = [HarmonyLib.HarmonyMethod]::new([DynamicCampaignJapanese.OriginalModIntegration].GetMethod('ManipulateCurrent'))
    $owner.Patch($refresh, $null, $null, $null, $null, $patch) | Out-Null
    $report.methodPatched = [DynamicCampaignJapanese.MethodIdentity]::Of($refresh)
    $report.localized = Test-CacheFixture
    $identity = [DynamicCampaignJapanese.MethodIdentity]::Of($refresh)
    $format = $catalog.Methods[$identity]['{0} ships in {1} task forces: {2} at sea, {3} in port · {4} short of fuel or ammunition']
    $expected = [string]::Format($format, [object[]]@(0, 2, 0, 0, 0))
    if (-not $report.original.rowPreserved -or -not $report.original.rebuiltWhenDataChanges) { throw 'The original fleet cache fixture is invalid.' }
    if ($report.localized.rows -ne 2 -or -not $report.localized.rowPreserved -or -not $report.localized.signatureEqual) { throw 'Unchanged fleet rows were rebuilt by localization.' }
    if (-not $report.localized.rebuiltWhenDataChanges) { throw 'A changed task force failed to rebuild its fleet row.' }
    if (-not $report.localized.taskForceStateUnchanged) { throw 'Refreshing localized fleet rows changed task force identity or state.' }
    if ($report.localized.summary -ne $expected) { throw 'The patched method did not produce the expected localized summary.' }
    $report.passed = $true
} catch {
    $report.error = $_.Exception.ToString() + ' AT ' + $_.ScriptStackTrace
}
$json = $report | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($ReportPath, $json, [Text.UTF8Encoding]::new($false))
$json
if (-not $report.passed) { exit 1 }
