<#
.SYNOPSIS
  Bật, tắt và xem trạng thái hệ thống Konnyaku trên máy dev bằng một lệnh.

.DESCRIPTION
  Gọi qua dev.cmd (không vướng Execution Policy):
    .\dev.cmd up           Bật hạ tầng Docker, build backend, migrate, chạy API + Worker + Web ở nền
    .\dev.cmd up -Seed     Như trên, rồi nạp dữ liệu review (chỉ cần một lần trên database mới)
    .\dev.cmd status       Thành phần nào đang chạy, cổng, địa chỉ mở trình duyệt
    .\dev.cmd logs api     Xem log (api | worker | web | migrator | build), Ctrl+C để thoát
    .\dev.cmd seed         Nạp dữ liệu review (API và Worker phải đang chạy)
    .\dev.cmd down         Tắt API, Worker, Web và dừng container (giữ dữ liệu)
    .\dev.cmd down -Free   Như trên, thêm tắt tiến trình build .NET còn sót để trả RAM
    .\dev.cmd reset -Yes   Tắt hết và XOÁ database dev (mất toàn bộ dữ liệu)

  Log và PID nằm trong thư mục .dev/ (đã gitignore). Cổng: Web 4000, API 5080, PostgreSQL 5433, Mailpit 8025.
#>
param(
  [Parameter(Position = 0)]
  [ValidateSet('up', 'down', 'status', 'logs', 'seed', 'reset', 'help')]
  [string]$Command = 'help',

  [Parameter(Position = 1)]
  [ValidateSet('api', 'worker', 'web', 'migrator', 'build')]
  [string]$Service = 'api',

  [switch]$Seed,      # up: nạp dữ liệu review sau khi bật
  [switch]$NoBuild,   # up: bỏ qua bước build backend (đã build rồi)
  [switch]$Free,      # down: tắt thêm các tiến trình build .NET
  [switch]$Force,     # down: tắt cả tiến trình lạ đang chiếm cổng 4000/5080
  [switch]$Yes        # reset: xác nhận xoá database
)

# Continue: native tools (docker, dotnet) write warnings to stderr, which PowerShell 5.1 would turn into terminating errors.
# Failures are checked explicitly through exit codes and Fail.
$ErrorActionPreference = 'Continue'
# SQL piped into docker (psql) must arrive as UTF-8.
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$Root = $PSScriptRoot
$DevDir = Join-Path $Root '.dev'
$Compose = Join-Path $Root 'infra/docker/docker-compose.yml'
$Infra = @('postgres', 'redis', 'mailpit')
$WebUrl = 'http://review.localhost:4000/login'

$Apps = [ordered]@{
  api    = @{ Port = 5080; Health = 'http://localhost:5080/health'; Label = 'API' }
  worker = @{ Port = $null; Health = $null; Label = 'Worker' }
  web    = @{ Port = 4000; Health = 'http://localhost:4000/api/health'; Label = 'Web' }
}

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }
function Fail([string]$text) { Write-Host "LỖI: $text" -ForegroundColor Red; exit 1 }

function Get-Dotnet {
  $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $fallback = 'C:\Program Files\dotnet\dotnet.exe'
  if (Test-Path $fallback) { return $fallback }
  Fail 'Không tìm thấy .NET SDK. Cài .NET 10 SDK rồi mở lại terminal.'
}

function Get-Python {
  foreach ($name in 'python', 'py', 'python3') {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    # Bỏ qua lối tắt Microsoft Store (WindowsApps) vì nó chỉ mở Store.
    if ($cmd -and $cmd.Source -notmatch 'WindowsApps') { return $cmd.Source }
  }
  Fail 'Không tìm thấy Python 3. Cài Python 3 rồi mở lại terminal.'
}

function Invoke-Docker([string[]]$arguments) {
  & docker @arguments
  if ($LASTEXITCODE -ne 0) { Fail "Lệnh docker $($arguments -join ' ') thất bại." }
}

function Assert-Docker {
  & docker info *> $null
  if ($LASTEXITCODE -ne 0) { Fail 'Docker chưa chạy. Mở Docker Desktop, đợi biểu tượng hết xoay rồi chạy lại.' }
}

function Get-PidFile([string]$name) { Join-Path $DevDir "$name.pid" }

function Get-AppProcess([string]$name) {
  $file = Get-PidFile $name
  if (-not (Test-Path $file)) { return $null }
  $id = [int](Get-Content $file -Raw)
  return Get-Process -Id $id -ErrorAction SilentlyContinue
}

function Get-PortOwner([int]$port) {
  $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $conn) { return $null }
  return Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
}

function Test-Url([string]$url) {
  try {
    $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3
    return $r.StatusCode -lt 500
  } catch { return $false }
}

function Stop-Tree([int]$id) {
  # dotnet run và npx đều sinh tiến trình con; /T tắt cả cây.
  & taskkill.exe /PID $id /T /F *> $null
}

function Start-App([string]$name, [string]$file, [string[]]$arguments, [string]$workDir) {
  $existing = Get-AppProcess $name
  if ($existing) { Write-Ok "$($Apps[$name].Label) đang chạy sẵn (PID $($existing.Id))"; return }
  $port = $Apps[$name].Port
  if ($port) {
    $owner = Get-PortOwner $port
    if ($owner) { Fail "Cổng $port đang bị $($owner.ProcessName) (PID $($owner.Id)) chiếm. Tắt nó, hoặc chạy .\dev.cmd down -Force." }
  }
  $log = Join-Path $DevDir "$name.log"
  $err = Join-Path $DevDir "$name.err.log"
  $p = Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $workDir `
    -RedirectStandardOutput $log -RedirectStandardError $err -WindowStyle Hidden -PassThru -ErrorAction Stop
  Set-Content -Path (Get-PidFile $name) -Value $p.Id -Encoding ascii
  Write-Ok "$($Apps[$name].Label) đã khởi động (PID $($p.Id)), log: .dev\$name.log"
}

function Wait-App([string]$name, [int]$seconds) {
  $app = $Apps[$name]
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    if (-not (Get-AppProcess $name)) {
      Write-Host ''
      Get-Content (Join-Path $DevDir "$name.log"), (Join-Path $DevDir "$name.err.log") -Tail 20 -ErrorAction SilentlyContinue
      Fail "$($app.Label) đã dừng khi đang khởi động. Xem log ở trên hoặc .\dev.cmd logs $name"
    }
    if ($app.Health) {
      if (Test-Url $app.Health) { Write-Ok "$($app.Label) sẵn sàng"; return }
    } else {
      # Worker không có cổng HTTP: chờ dòng "Application started" trong log.
      if (Select-String -Path (Join-Path $DevDir "$name.log") -Pattern 'Application started' -Quiet -ErrorAction SilentlyContinue) {
        Write-Ok "$($app.Label) sẵn sàng"; return
      }
    }
    Start-Sleep -Seconds 2
  }
  Fail "$($app.Label) chưa sẵn sàng sau $seconds giây. Xem .\dev.cmd logs $name"
}

function Wait-Postgres {
  for ($i = 0; $i -lt 40; $i++) {
    & docker compose -f $Compose exec -T postgres pg_isready -U konnyaku -d konnyaku *> $null
    if ($LASTEXITCODE -eq 0) { Write-Ok 'PostgreSQL sẵn sàng (cổng 5433)'; return }
    Start-Sleep -Seconds 2
  }
  Fail 'PostgreSQL chưa sẵn sàng sau 80 giây. Xem: docker compose -f infra/docker/docker-compose.yml logs postgres'
}

function Invoke-Psql([string]$sql) {
  $out = $sql | & docker compose -f $Compose exec -T postgres psql -U konnyaku -d konnyaku -v ON_ERROR_STOP=1 -qAt 2>&1
  if ($LASTEXITCODE -ne 0) { $out | Write-Host; Fail 'Lệnh SQL trên database dev thất bại.' }
  return $out | Where-Object { $_ -is [string] -and $_ -ne '' }
}

function Sync-ModuleRoles {
  # The init script only runs on a new volume. Re-applying it (idempotent) before migrations gives a module added
  # since then its role, so the Migrator's default privileges cover its new tables.
  Invoke-Psql (Get-Content (Join-Path $Root 'infra/docker/postgres/init/01-modules.sql') -Raw -Encoding UTF8) | Out-Null
}

function Assert-ModuleGrants {
  # Tables created while their module role did not exist yet are unreadable for that role (default privileges came later).
  $missing = Invoke-Psql @"
SELECT DISTINCT r.rolname FROM pg_roles r
CROSS JOIN pg_namespace n JOIN pg_class c ON c.relnamespace = n.oid AND c.relkind IN ('r', 'p')
WHERE r.rolname LIKE 'kk\_%' AND n.nspname NOT IN ('public', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'
  AND has_schema_privilege(r.oid, n.oid, 'USAGE') AND NOT has_table_privilege(r.oid, c.oid, 'SELECT')
ORDER BY 1;
"@
  if ($missing) {
    Write-Warn "Database dev được tạo trước khi có module: $($missing -join ', ') không đọc được bảng của mình."
    Write-Warn 'Cách sửa (xoá dữ liệu dev rồi tạo lại kèm dữ liệu mẫu): .\dev.cmd reset -Yes  rồi  .\dev.cmd up -Seed'
  }
}

function Invoke-Logged([string]$name, [string]$file, [string[]]$arguments) {
  $log = Join-Path $DevDir "$name.log"
  & $file @arguments *> $log
  if ($LASTEXITCODE -ne 0) {
    Get-Content $log -Tail 30
    Fail "Bước $name thất bại (log đầy đủ: .dev\$name.log)."
  }
}

function Invoke-Seed {
  if (-not (Test-Url $Apps.api.Health)) { Fail 'API chưa chạy. Chạy .\dev.cmd up trước.' }
  if (-not (Get-AppProcess 'worker')) { Write-Warn 'Không thấy Worker do dev.ps1 bật; seed cần Worker đang chạy.' }
  Write-Step 'Nạp dữ liệu review (1–2 phút)'
  $python = Get-Python
  & $python (Join-Path $Root 'infra/scripts/seed_review.py')
  if ($LASTEXITCODE -ne 0) { Fail 'Seed thất bại. Nếu database đã có trung tâm review thì không cần seed lại.' }
}

function Show-Status {
  Write-Step 'Trạng thái'
  $rows = foreach ($name in $Apps.Keys) {
    $app = $Apps[$name]
    $p = Get-AppProcess $name
    $state = if ($p) { if (-not $app.Health -or (Test-Url $app.Health)) { 'đang chạy' } else { 'đang khởi động' } } else { 'tắt' }
    if (-not $p -and $app.Port) {
      $owner = Get-PortOwner $app.Port
      if ($owner) { $state = "cổng bị $($owner.ProcessName) PID $($owner.Id) chiếm (không do dev.ps1 bật)" }
    }
    [pscustomobject]@{ 'Thành phần' = $app.Label; 'Cổng' = $app.Port; 'Trạng thái' = $state; 'PID' = $(if ($p) { $p.Id } else { '' }) }
  }
  $rows | Format-Table -AutoSize | Out-String | Write-Host
  & docker info *> $null
  if ($LASTEXITCODE -eq 0) {
    & docker compose -f $Compose ps --format 'table {{.Service}}\t{{.Status}}\t{{.Ports}}' 2>$null
  } else {
    Write-Warn 'Docker chưa chạy.'
  }
  Write-Host ''
  Write-Host "Mở trình duyệt: $WebUrl   ·   Email: http://localhost:8025   ·   API docs: http://localhost:5080/scalar"
}

function Stop-Apps {
  foreach ($name in @('web', 'worker', 'api')) {
    $p = Get-AppProcess $name
    if ($p) { Stop-Tree $p.Id; Write-Ok "Đã tắt $($Apps[$name].Label)" }
    Remove-Item (Get-PidFile $name) -ErrorAction SilentlyContinue
    $port = $Apps[$name].Port
    if ($port) {
      $owner = Get-PortOwner $port
      if ($owner) {
        if ($Force) { Stop-Tree $owner.Id; Write-Ok "Đã tắt $($owner.ProcessName) (PID $($owner.Id)) đang chiếm cổng $port" }
        else { Write-Warn "Cổng $port vẫn bị $($owner.ProcessName) (PID $($owner.Id)) chiếm, không do dev.ps1 bật. Thêm -Force để tắt luôn." }
      }
    }
  }
}

New-Item -ItemType Directory -Force -Path $DevDir | Out-Null

switch ($Command) {
  'up' {
    Assert-Docker
    Write-Step 'Bật hạ tầng Docker (PostgreSQL, Redis, Mailpit)'
    Invoke-Docker (@('compose', '-f', $Compose, 'up', '-d') + $Infra)
    Wait-Postgres

    $dotnet = Get-Dotnet
    if (-not $NoBuild) {
      Write-Step 'Build backend (lần đầu vài phút)'
      Invoke-Logged 'build' $dotnet @('build', (Join-Path $Root 'backend/Konnyaku.slnx'), '--nologo', '-v', 'q')
      Write-Ok 'Build xong'
    }

    Write-Step 'Cập nhật bảng trong database (role từng module, rồi migration)'
    Sync-ModuleRoles
    Invoke-Logged 'migrator' $dotnet @('run', '--no-build', '--project', (Join-Path $Root 'backend/src/Hosts/Konnyaku.Migrator'))
    Write-Ok 'Database đã cập nhật'
    Assert-ModuleGrants

    if (-not (Test-Path (Join-Path $Root 'frontend/node_modules'))) {
      Write-Step 'Cài thư viện frontend (lần đầu)'
      Push-Location (Join-Path $Root 'frontend')
      try { & npx.cmd -y pnpm@10.34.9 install; if ($LASTEXITCODE -ne 0) { Fail 'pnpm install thất bại.' } }
      finally { Pop-Location }
    }

    Write-Step 'Chạy API, Worker, Web ở nền'
    Start-App 'api' $dotnet @('run', '--no-build', '--project', 'backend/src/Hosts/Konnyaku.Api', '--launch-profile', 'http') $Root
    Start-App 'worker' $dotnet @('run', '--no-build', '--project', 'backend/src/Hosts/Konnyaku.Worker') $Root
    Start-App 'web' 'cmd.exe' @('/c', 'npx -y pnpm@10.34.9 dev') (Join-Path $Root 'frontend/apps/site')
    Wait-App 'api' 120
    Wait-App 'worker' 120
    Wait-App 'web' 180

    if ($Seed) { Invoke-Seed }
    Write-Host ''
    Write-Host "Xong. Mở $WebUrl" -ForegroundColor Green
    Write-Host 'Tài khoản thử: docs/chay-local.md. Tắt: .\dev.cmd down'
  }
  'down' {
    Write-Step 'Tắt API, Worker, Web'
    Stop-Apps
    & docker info *> $null
    if ($LASTEXITCODE -eq 0) {
      Write-Step 'Dừng container (dữ liệu vẫn giữ)'
      & docker compose -f $Compose stop
    }
    if ($Free) {
      Write-Step 'Tắt tiến trình build .NET còn sót'
      & (Get-Dotnet) build-server shutdown *> $null
      Write-Ok 'Đã tắt. Muốn trả nốt RAM của Docker: wsl --shutdown'
    }
  }
  'status' { Show-Status }
  'logs' {
    $log = Join-Path $DevDir "$Service.log"
    if (-not (Test-Path $log)) { Fail "Chưa có log $Service. Chạy .\dev.cmd up trước." }
    $err = Join-Path $DevDir "$Service.err.log"
    if ((Test-Path $err) -and (Get-Item $err).Length -gt 0) {
      Write-Step "Lỗi gần nhất (.dev\$Service.err.log)"
      Get-Content $err -Tail 20
    }
    Write-Step "Log $Service (Ctrl+C để thoát)"
    Get-Content $log -Tail 50 -Wait
  }
  'seed' { Invoke-Seed }
  'reset' {
    if (-not $Yes) { Fail 'Lệnh này XOÁ toàn bộ database dev. Chạy lại với -Yes nếu chắc chắn: .\dev.cmd reset -Yes' }
    Write-Step 'Tắt ứng dụng'
    Stop-Apps
    Assert-Docker
    Write-Step 'Xoá container và dữ liệu'
    Invoke-Docker @('compose', '-f', $Compose, 'down', '-v')
    Write-Ok 'Đã xoá. Bật lại kèm dữ liệu mẫu: .\dev.cmd up -Seed'
  }
  default { Get-Help $PSCommandPath -Detailed | Out-String | Write-Host }
}
