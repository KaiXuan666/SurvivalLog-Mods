param([Parameter(Mandatory=$true)][string]$Plugin,[Parameter(Mandatory=$true)][string]$GameDir)
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')
$resolver=[Mono.Cecil.DefaultAssemblyResolver]::new()
foreach($dir in @('BepInEx\interop','BepInEx\core','dotnet')){$resolver.AddSearchDirectory((Join-Path $GameDir $dir))}
$rp=[Mono.Cecil.ReaderParameters]::new();$rp.AssemblyResolver=$resolver
$a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $Plugin).Path,$rp)
$missing=@();$count=0
foreach($r in $a.MainModule.GetMemberReferences()){
 if($r.DeclaringType.Scope.Name -notin @('HotUpdate','Vuplex.WebView','UnityEngine.CoreModule','UnityEngine.InputLegacyModule','R3','ObservableCollections')){continue}
 $count++
 try{$found=$r.Resolve();if($null -eq $found){$missing+=$r.FullName}}catch{$missing+=$r.FullName+' ERROR '+$_.Exception.Message}
}
[pscustomobject]@{Plugin=$Plugin;Checked=$count;Missing=$missing} | ConvertTo-Json -Depth 3
$a.Dispose();$resolver.Dispose()
