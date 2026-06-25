<#
  Agen-Link - one-time setup. Builds the MCP server and the Terminal pty-host,
  and installs the GitHub CLI (gh).

  EASIEST: double-click  install\setup.cmd  - it runs this script with the right
  flags and keeps the window open so you can read the result.

  Or run from a terminal:
      powershell -ExecutionPolicy Bypass -File "C:\path\to\Agen-Link\install\setup.ps1"

  If Windows refuses to run this ("...cannot be loaded because running scripts is
  disabled" / "is not digitally signed"), that is the download block (execution
  policy + Mark of the Web). Use setup.cmd or the -ExecutionPolicy Bypass command
  above - both get past it. Do NOT just double-click / "Run with PowerShell" the
  .ps1 from a fresh download; it will fail and the window will close instantly.
#>

$ErrorActionPreference = 'Stop'

try {
    $root = Split-Path $PSScriptRoot -Parent          # repo root (parent of install/)
    $mcp  = Join-Path $root 'mcp-server'
    $pty  = Join-Path $root 'pty-host'

    Write-Host "== Agen-Link setup ==" -ForegroundColor Cyan
    Write-Host "Repo root: $root`n"

    # ---- 1. Build the MCP server -------------------------------------------------
    Write-Host "[1/5] Building the MCP server..." -ForegroundColor Cyan
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw "Node.js not found. Install Node 18+ first (https://nodejs.org), open a new window, and re-run." }
    if (-not (Get-Command npm  -ErrorAction SilentlyContinue)) { throw "npm not found. Install Node 18+ (it includes npm), open a new window, and re-run." }

    Push-Location $mcp
    try {
        npm install
        if ($LASTEXITCODE -ne 0) { throw "npm install failed in mcp-server (exit $LASTEXITCODE). Check your internet connection and Node version (need 18+)." }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed in mcp-server (exit $LASTEXITCODE)." }
    }
    finally { Pop-Location }

    $index = Join-Path $mcp 'build\index.js'
    if (Test-Path $index) { Write-Host "  OK: $index" -ForegroundColor Green }
    else { throw "MCP server build failed (missing build/index.js)." }

    # ---- 2. Install the pty-host (Terminal tab) ----------------------------------
    # Plain index.js (no build step), but it needs node-pty installed. node-pty ships
    # prebuilt binaries, so this is a download with no compiler required.
    Write-Host "`n[2/5] Installing the pty-host (Terminal)..." -ForegroundColor Cyan
    Push-Location $pty
    try {
        npm install
        if ($LASTEXITCODE -ne 0) { throw "npm install failed in pty-host (exit $LASTEXITCODE). node-pty downloads a prebuilt binary - check your internet connection." }
    }
    finally { Pop-Location }

    $ptyEntry = Join-Path $pty 'index.js'
    $ptyDep   = Join-Path $pty 'node_modules\node-pty'
    if ((Test-Path $ptyEntry) -and (Test-Path $ptyDep)) { Write-Host "  OK: $ptyEntry" -ForegroundColor Green }
    else { throw "pty-host setup failed (missing index.js or node-pty)." }

    # ---- 3. Install the GitHub CLI (gh) -----------------------------------------
    Write-Host "`n[3/5] GitHub CLI (gh)..." -ForegroundColor Cyan
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        Write-Host "  Already installed: $((Get-Command gh).Source)" -ForegroundColor Green
    }
    elseif (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host "  Installing via winget (a UAC prompt may appear)..."
        winget install --id GitHub.cli -e --source winget --accept-package-agreements --accept-source-agreements
        Write-Host "  Installed. Restart Unity afterwards so it sees gh on PATH." -ForegroundColor Yellow
    }
    else {
        Write-Host "  winget not found. Install gh manually from https://cli.github.com/ then run 'gh auth login'." -ForegroundColor Yellow
    }

    # ---- 4. Antigravity CLI (optional) ------------------------------------------
    # The Terminal tab can run Claude OR Antigravity (agy) - pick it in Settings. Antigravity
    # installs via Google's own installer (not npm), so we only check for it here.
    Write-Host "`n[4/5] Antigravity CLI (agy, optional)..." -ForegroundColor Cyan
    $agyExe = Join-Path $env:LOCALAPPDATA 'agy\bin\agy.exe'
    if ((Get-Command agy -ErrorAction SilentlyContinue) -or (Test-Path $agyExe)) {
        Write-Host "  Found (agy is installed)." -ForegroundColor Green
    }
    else {
        Write-Host "  Not found. To run Antigravity in the Terminal tab, install the Antigravity CLI from" -ForegroundColor Yellow
        Write-Host "  https://antigravity.google/docs/cli-install , then run 'agy' once to sign in." -ForegroundColor Yellow
    }

    # ---- 5. Next steps -----------------------------------------------------------
    Write-Host "`n[5/5] Done. Next steps:" -ForegroundColor Cyan
    Write-Host @"
  1) In each Unity project you want to use this in:
       Window > Package Manager > '+' > 'Add package from disk...'
       select:  $root\unity-package\package.json
  2) Open the tool:  Window > Agen-Link
  3) (Optional) GitHub backup: run 'gh auth login' in a terminal, then use the GitHub tab.
  4) Settings tab: pick the CLI (Claude or Antigravity). Both get the Unity bridge + shared memory.
  5) Go to the "Terminal" tab, press "Start session", then type a prompt.
"@ -ForegroundColor Gray
}
catch {
    Write-Host "`n== SETUP FAILED ==" -ForegroundColor Red
    Write-Host ("  " + $_.Exception.Message) -ForegroundColor Red
    Write-Host "  See INSTALL.txt for prerequisites (Node 18+, internet). Fix the above, then re-run setup." -ForegroundColor Yellow
    $script:setupFailed = $true
}
finally {
    # Keep the window open so the result (success OR error) is readable. The setup.cmd
    # launcher sets AGENLINK_LAUNCHER and does its own pause, so skip this one then.
    if (-not $env:AGENLINK_LAUNCHER -and [Environment]::UserInteractive) {
        Write-Host ""
        $null = Read-Host "Press Enter to close"
    }
}

if ($script:setupFailed) { exit 1 }
