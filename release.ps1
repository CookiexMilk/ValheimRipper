# Builds both downloads into release\: ValheimRipper.zip (everything included) and ValheimRipper-dotnet.zip (needs the
# .NET Desktop Runtime 8 or newer). Each zip holds ValheimRipper.exe and THIRD-PARTY-NOTICES.md. Run: .\release.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet publish -c Release -o publish -nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet publish -c Release -o publish-dotnet -p:Small=true -nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force release | Out-Null
foreach ($build in @(@("publish", "ValheimRipper.zip"), @("publish-dotnet", "ValheimRipper-dotnet.zip"))) {
	$zip = Join-Path "release" $build[1]
	if (Test-Path $zip) { Remove-Item $zip }
	Compress-Archive -Path (Join-Path $build[0] "ValheimRipper.exe"), "THIRD-PARTY-NOTICES.md" -DestinationPath $zip
}
Get-ChildItem release | Format-Table Name, Length
