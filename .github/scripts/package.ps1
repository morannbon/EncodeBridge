param(
    [Parameter(Mandatory=$true)][ValidateSet('x64','x86')][string]$Platform,
    [Parameter(Mandatory=$true)][string]$RefName
)
$ErrorActionPreference='Stop'
[xml]$project=Get-Content -LiteralPath 'EncodeBridge/EncodeBridge.csproj'
$version=[string]$project.Project.PropertyGroup[0].Version
if(!$version){throw 'Product version missing'}
if($RefName.StartsWith('v') -and $RefName -ne "v$version"){throw 'Tag and product version differ'}
$dll="EncodeBridge/bin/$Platform/Release/net8.0-windows/EncodeBridge.dll"
if(!(Test-Path -LiteralPath $dll)){throw 'Plugin DLL missing'}
$bytes=[IO.File]::ReadAllBytes([IO.Path]::GetFullPath($dll))
$utf8=[Text.Encoding]::UTF8.GetString($bytes)
$utf16=[Text.Encoding]::Unicode.GetString($bytes)
foreach($marker in @('WriteDeveloperDiagnostic','DiagnosticCommandLine','DiagnosticQuote','SnapshotUiWindowIdsForDiagnostic','ENCODEBRIDGE_STATE_LOAD','ENCODEBRIDGE_EXTERNAL_COMMAND')) {
    if($utf8.Contains($marker) -or $utf16.Contains($marker)){throw "Developer diagnostics remain: $marker"}
}
if((Get-FileHash README.md).Hash -ne (Get-FileHash README.txt).Hash){throw 'README files differ'}
$out="package/EncodeBridge_v${version}_${Platform}"
if(Test-Path -LiteralPath $out){throw 'Package staging already exists. Use a clean checkout.'}
New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $out
foreach($doc in @('README.md','README.txt','LICENSE','SDK_LICENSE.txt')){Copy-Item -LiteralPath $doc -Destination $out}
@"
EncodeBridge v$version ($Platform) - ビルド済み配布

このZIPはビルド済みです。Visual Studioによるビルドは不要です。
TvAIr 1.2.4以上と、TvAIrと同じアーキテクチャの配布物を使用してください。
TvAIrを終了してから、EncodeBridge.dllだけをTvAIr.exeと同じフォルダーのPluginsへ配置してください。
TvAIrを起動し、PluginメニューにEncodeBridgeが表示されることを確認してください。
更新時もTvAIrを終了してDLLを置き換えます。設定や履歴を削除する必要はありません。
外部の変換アプリは別途用意し、READMEの手順で設定してください。
ソースからのビルド手順はREADMEに記載しています。
"@ | Set-Content -LiteralPath (Join-Path $out 'INSTALL.txt') -Encoding UTF8
Compress-Archive -Path "$out/*" -DestinationPath "package/EncodeBridge_v${version}_${Platform}.zip"
