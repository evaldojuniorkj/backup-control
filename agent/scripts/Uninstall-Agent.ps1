#Requires -RunAsAdministrator
$ErrorActionPreference='Stop'
$name='BackupControlAgent'
if (Get-Service $name -ErrorAction SilentlyContinue) {
  Stop-Service $name
  & sc.exe delete $name
  if ($LASTEXITCODE -ne 0) { throw 'Falha ao remover o serviço.' }
}
Write-Host 'Serviço removido. Executável, configuração protegida, estado e arquivos de backup foram preservados.'
