#Requires -RunAsAdministrator
[CmdletBinding()]
param(
  [string]$PackageDirectory = (Split-Path $PSScriptRoot -Parent),
  [string]$InstallDirectory = "$env:ProgramFiles\BackupControlAgent",
  [string]$DataDirectory = "$env:ProgramData\BackupControlAgent",
  [string]$SqlServiceIdentity = 'NT SERVICE\MSSQLSERVER',
  [PSCredential]$ServiceCredential,
  [switch]$SkipConfigure
)
$ErrorActionPreference='Stop'
$serviceName='BackupControlAgent'
$source=[IO.Path]::GetFullPath($PackageDirectory)
$target=[IO.Path]::GetFullPath($InstallDirectory)
$data=[IO.Path]::GetFullPath($DataDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $source 'BackupControl.Agent.exe'))) { throw 'Pacote inválido: executável não encontrado.' }
if (Get-Service $serviceName -ErrorAction SilentlyContinue) { throw 'O serviço já existe. Pare-o e faça uma atualização planejada; este script não substitui uma instalação existente.' }
if ($target -eq [IO.Path]::GetPathRoot($target) -or $data -eq [IO.Path]::GetPathRoot($data)) { throw 'Não use a raiz de um disco como diretório.' }
if ($target.Equals($source,[StringComparison]::OrdinalIgnoreCase) -or $target.StartsWith($source.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'A pasta de instalação deve ficar fora do pacote de origem.' }
New-Item -ItemType Directory -Force -Path $target,$data | Out-Null
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $target -Recurse -Force
$exe=Join-Path $target 'BackupControl.Agent.exe'
if (-not $SkipConfigure) { & $exe --configure --data-dir $data; if ($LASTEXITCODE -ne 0) { throw 'Configuração não concluída.' } }
if (-not (Test-Path -LiteralPath (Join-Path $data 'settings.dat'))) { throw 'Execute --configure para criar a configuração protegida.' }
$binary='"'+$exe+'" --data-dir "'+$data+'"'
$serviceParameters=@{Name=$serviceName;BinaryPathName=$binary;DisplayName='Backup Control Agent';StartupType='Automatic';Description='Agente de backup SQL Server e envio FTP controlado pela central.'}
if ($ServiceCredential) { $serviceParameters.Credential=$ServiceCredential }
New-Service @serviceParameters | Out-Null
if (-not $ServiceCredential) {
  & sc.exe config $serviceName obj= "NT SERVICE\$serviceName" | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Não foi possível definir a conta virtual do serviço.' }
}
$identity=if($ServiceCredential){$ServiceCredential.UserName}else{"NT SERVICE\$serviceName"}
# Estado e configuração ficam restritos; o SQL recebe acesso apenas à pasta de arquivos.
& icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${identity}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Falha ao proteger o diretório de dados.' }
$paths=& $exe --paths --data-dir $data | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível ler o caminho local configurado.' }
$backup=$paths.backupDirectory
if (-not [IO.Path]::IsPathFullyQualified($backup)) { throw 'Use uma pasta absoluta.' }
$backup=[IO.Path]::GetFullPath($backup)
if ($backup -eq $data -or $backup -eq $target -or $backup -eq [IO.Path]::GetPathRoot($backup)) { throw 'Use uma pasta exclusiva para os arquivos de backup.' }
if ($data.StartsWith($backup.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $target.StartsWith($backup.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'A pasta de backups não pode conter as pastas de configuração ou instalação.' }
New-Item -ItemType Directory -Force -Path $backup | Out-Null
& icacls.exe $backup /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${identity}:(OI)(CI)M" "${SqlServiceIdentity}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Falha ao conceder acesso à pasta de backups.' }
& sc.exe failure $serviceName reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null
if (-not [System.Diagnostics.EventLog]::SourceExists('BackupControlAgent')) { [System.Diagnostics.EventLog]::CreateEventSource('BackupControlAgent','Application') }
Write-Host "Serviço instalado e parado. Conta: $identity"
Write-Host 'Antes de iniciar, autorize a conta no SQL Server ou use um login SQL configurado no agente.'
Write-Host "Iniciar: Start-Service $serviceName"
Write-Host 'Não foram concedidas permissões SQL automaticamente.'
