# Builds AutoClicker.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK or Visual Studio needed. Output: ..\dist\AutoClicker.exe
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $PSScriptRoot "..\dist"
New-Item -ItemType Directory -Force $out | Out-Null
& $csc /nologo /target:winexe /optimize+ /out:"$out\AutoClicker.exe" `
    /win32manifest:"$PSScriptRoot\app.manifest" `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    "$PSScriptRoot\Core.cs" "$PSScriptRoot\PointEditor.cs" "$PSScriptRoot\MainForm.cs"
if ($LASTEXITCODE -eq 0) { Write-Host "Built $out\AutoClicker.exe" }
