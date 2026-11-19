param([Parameter(Mandatory=$true)][string]$SigningKey)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$agent=Join-Path $project 'artifacts\windows-agent'
$distribution=Join-Path $project 'artifacts\distribution'
New-Item -ItemType Directory -Force (Join-Path $distribution 'packages') | Out-Null
& 'C:\Program Files\dotnet\dotnet.exe' publish (Join-Path $project 'agent\BackupControl.Agent.csproj') -c Release -r win-x64 --self-contained true -o $agent --verbosity quiet
if($LASTEXITCODE){throw 'Agent build failed'}
$releaseVersion=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $agent 'BackupControl.Agent.dll')).Version.ToString(3)
[IO.File]::WriteAllText((Join-Path $agent 'agent-release.json'),(@{version=$releaseVersion;updateProtocol=1}|ConvertTo-Json -Compress))
$manifest=[ordered]@{}
Get-ChildItem -LiteralPath $agent -File -Recurse | Where-Object Name -NotIn @('package-manifest.json','package-manifest.sig') | Sort-Object FullName | ForEach-Object { $name=[IO.Path]::GetRelativePath($agent,$_.FullName).Replace('\','/'); $manifest[$name]=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
$bytes=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
[IO.File]::WriteAllBytes((Join-Path $agent 'package-manifest.json'),$bytes)
$rsa=[Security.Cryptography.RSA]::Create();$rsa.ImportPkcs8PrivateKey([IO.File]::ReadAllBytes((Resolve-Path $SigningKey)),[ref]([int]$read=0))
try{[IO.File]::WriteAllBytes((Join-Path $agent 'package-manifest.sig'),$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1))}finally{$rsa.Dispose()}
$zip=Join-Path $distribution 'backup-control-agent-win-x64.zip'
$temporary=$zip+'.tmp'
if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}
[IO.Compression.ZipFile]::CreateFromDirectory($agent,$temporary,[IO.Compression.CompressionLevel]::Optimal,$false)
$digest=(Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
$snapshot=Join-Path $distribution ('packages\'+$digest+'.zip')
if(!(Test-Path -LiteralPath $snapshot)){Copy-Item -LiteralPath $temporary -Destination $snapshot}
Move-Item -LiteralPath $temporary -Destination $zip -Force
& 'C:\Program Files\dotnet\dotnet.exe' publish (Join-Path $project 'installer\BackupControl.Setup.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $project 'artifacts\setup-template') --verbosity quiet
if($LASTEXITCODE){throw 'Installer build failed'}
Copy-Item -LiteralPath (Join-Path $project 'artifacts\setup-template\BackupControl.Setup.exe') -Destination (Join-Path $distribution 'BackupControl.Setup.exe.tmp') -Force
Move-Item -LiteralPath (Join-Path $distribution 'BackupControl.Setup.exe.tmp') -Destination (Join-Path $distribution 'BackupControl.Setup.exe') -Force
[IO.File]::WriteAllText((Join-Path $distribution 'agent.sha256'),$digest)
[IO.File]::WriteAllText((Join-Path $distribution 'release.json'),(@{version=$releaseVersion;sha256=$digest}|ConvertTo-Json -Compress))
Write-Output ('Published signed package SHA256 '+$digest)
