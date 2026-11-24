<#
.SYNOPSIS
    Makes the before/after page a UI change ships with: SCREENSHOTS/{YYMMDD}-{feature}.html plus one annotated PNG.

.DESCRIPTION
    Everything runs LOCALLY; the live site is never touched.

    1. Each side is published like scripts/run-e2eui.ps1 does it (trimmed Release, `-p:WasmApplicationEnvironmentName=Test`
       so the guest sign-in exists) and started as ASPNETCORE_ENVIRONMENT=Test with UseMockData=true (no secrets, nothing
       billed) on http://localhost:<Port>, with its own Azurite container on 21030-21032.
       -Before is a git ref (default HEAD~1), checked out in a temporary detached worktree. -After is a ref too, or
       the working tree when left empty. -BeforeDir / -AfterDir skip the build and read PNGs instead
       (`<screen>_<width>.png`, or the v4 audit's `*-<width>_<route-slug>.png`).
    2. Every screen is shot at 1366x768 and 390x844 with the E2EUI project's Playwright (Chromium, reduced motion).
       Signed-in screens go through the guest door `/auth/login/fake?returnUrl=<route>` (Test host only).
    3. Before and after are compared pixel by pixel; changed areas get a red box on the after shot.
    4. Writes SCREENSHOTS/<yyMMdd>-<Feature>.html (self-contained; unchanged screens shown once, small) and
       SCREENSHOTS/<yyMMdd>-<Feature>.png (the most-changed screens side by side with the notes, kept under ~300 KB).

    SCREENSHOTS/ is git-ignored (AGENTS.md section 4): the page and PNG are local review aids; give their paths.
    Needs Docker (Azurite) and Playwright Chromium (`pwsh tests/PoSeeReview.E2EUI/bin/Release/net10.0/playwright.ps1 install`).

.PARAMETER Screens
    name=in:/route (signed in as the guest) or name=out:/route (signed out).

.EXAMPLE
    pwsh -NoProfile -File scripts/screenshots.ps1 -Feature comic-card -Notes 'The comic card lost its border.'

.EXAMPLE
    pwsh -NoProfile -File scripts/screenshots.ps1 -Feature v4-audit -Before 8817fed -After 5d76f4b
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string] $Feature,
    [string] $Before = 'HEAD~1',
    [string] $BeforeDir = '',
    [string] $After = '',
    [string] $AfterDir = '',
    [string[]] $Screens = @('login=out:/login', 'diag=out:/diag', 'home=in:/', 'leaderboard=in:/leaderboard',
        'my-comics=in:/my-comics', 'settings=in:/settings', 'not-found=in:/no-such-page'),
    [string[]] $Notes = @(),
    [string] $Date = (Get-Date).ToString('yyMMdd'),
    [int] $Port = 7319,
    [switch] $KeepWork
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'SCREENSHOTS'
$work = Join-Path ([IO.Path]::GetTempPath()) "poseereview-shots-$PID"
$baseUrl = "http://localhost:$Port"
$viewports = @(@{ W = 1366; H = 768 }, @{ W = 390; H = 844 })

$screenList = foreach ($s in $Screens) {
    if ($s -notmatch '^([a-z0-9-]+)=(in|out):(/\S*)$') { throw "Screen '$s' must be name=in:/route or name=out:/route" }
    [pscustomobject]@{ Name = $Matches[1]; SignedIn = $Matches[2] -eq 'in'; Route = $Matches[3] }
}

function Test-Listening([int] $p) {
    $c = [Net.Sockets.TcpClient]::new()
    try { $c.ConnectAsync('127.0.0.1', $p).Wait(1000) -and $c.Connected } catch { $false } finally { $c.Dispose() }
}
function Await($task) { $task.GetAwaiter().GetResult() }
if (Test-Listening $Port) { throw "Port $Port is taken; pass -Port." }

$playwrightDll = Join-Path $root 'tests/PoSeeReview.E2EUI/bin/Release/net10.0/Microsoft.Playwright.dll'
if (-not (Test-Path $playwrightDll)) {
    dotnet build (Join-Path $root 'tests/PoSeeReview.E2EUI') -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE) { throw 'building tests/PoSeeReview.E2EUI failed' }
}
Add-Type -Path $playwrightDll

New-Item -ItemType Directory -Force $work | Out-Null
$worktrees = [Collections.Generic.List[string]]::new()
$playwright = $null; $browser = $null
try {
    # Its own Azurite on 21030-21032, so it never shares storage with the test runners or a dev Azurite.
    $container = "poseereview-shots-azurite-$PID"
    if (-not ($BeforeDir -and $AfterDir)) {
        docker info *> $null
        if ($LASTEXITCODE) { throw 'Docker is not running (Azurite needs it).' }
        docker run -d --rm --name $container -p 21030:10000 -p 21031:10001 -p 21032:10002 `
            mcr.microsoft.com/azure-storage/azurite:latest azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 `
            --tableHost 0.0.0.0 --skipApiVersionCheck --loose | Out-Null
        if ($LASTEXITCODE) { throw 'Azurite did not start' }
    }
    $playwright = Await ([Microsoft.Playwright.Playwright]::CreateAsync())
    $browser = Await ($playwright.Chromium.LaunchAsync([Microsoft.Playwright.BrowserTypeLaunchOptions]@{ Headless = $true }))

    function Get-SourceDir([string] $ref, [string] $side) {
        if (-not $ref) { return $root }
        $dir = Join-Path $work "src-$side"
        git -C $root worktree add --detach $dir $ref 2>&1 | Out-Null
        if ($LASTEXITCODE) { throw "git worktree add $ref failed" }
        $worktrees.Add($dir)
        return $dir
    }

    function Invoke-Shots([string] $src, [string] $side) {
        $pub = Join-Path $work "publish-$side"
        Write-Host "[$side] publishing $src" -ForegroundColor Cyan
        dotnet publish (Join-Path $src 'src/PoSeeReview.Api') -c Release -o $pub -p:WasmApplicationEnvironmentName=Test --nologo -v q | Out-Null
        if ($LASTEXITCODE) { throw "[$side] publish failed" }
        $env:ASPNETCORE_ENVIRONMENT = 'Test'
        $env:ASPNETCORE_URLS = $baseUrl
        $env:UseMockData = 'true'
        $env:KeyVault__Uri = ''
        $env:Storage__UseAzurite = 'true'
        $env:Storage__AzuritePorts = '21030,21031,21032'
        $app = Start-Process dotnet -ArgumentList 'PoSeeReview.Api.dll' -WorkingDirectory $pub -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $work "$side-app.log") -RedirectStandardError (Join-Path $work "$side-app.err.log")
        $shots = Join-Path $work "shots-$side"
        New-Item -ItemType Directory -Force $shots | Out-Null
        try {
            $handler = [Net.Http.HttpClientHandler]::new()
            $handler.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
            $http = [Net.Http.HttpClient]::new($handler)
            $up = $false
            foreach ($i in 1..90) {
                if ($app.HasExited) { throw "[$side] the app exited while starting; see $work\$side-app.log" }
                try { if ($http.GetAsync("$baseUrl/health/live").Result.IsSuccessStatusCode) { $up = $true; break } } catch { }
                Start-Sleep -Seconds 2
            }
            if (-not $up) { throw "[$side] no answer from $baseUrl/health/live within 180 s" }
            foreach ($s in $screenList) {
                foreach ($v in $viewports) {
                    $ctx = Await ($browser.NewContextAsync([Microsoft.Playwright.BrowserNewContextOptions]@{
                        ViewportSize = [Microsoft.Playwright.ViewportSize]@{ Width = $v.W; Height = $v.H }
                        DeviceScaleFactor = 1; ReducedMotion = [Microsoft.Playwright.ReducedMotion]::Reduce
                    }))
                    try {
                        $page = Await ($ctx.NewPageAsync())
                        $url = if ($s.SignedIn) { "$baseUrl/auth/login/fake?returnUrl=$([uri]::EscapeDataString($s.Route))" } else { "$baseUrl$($s.Route)" }
                        Await ($page.GotoAsync($url, [Microsoft.Playwright.PageGotoOptions]@{ WaitUntil = [Microsoft.Playwright.WaitUntilState]::NetworkIdle; Timeout = 90000 })) | Out-Null
                        Await ($page.WaitForSelectorAsync('.po-header', [Microsoft.Playwright.PageWaitForSelectorOptions]@{ Timeout = 60000 })) | Out-Null
                        if ($s.Route -eq '/diag') {
                            # Wait for the probes, not for the "Checking" placeholder.
                            try { Await ($page.WaitForFunctionAsync("() => !document.body.innerText.includes('Checking')", $null, [Microsoft.Playwright.PageWaitForFunctionOptions]@{ Timeout = 60000 })) | Out-Null } catch { }
                        }
                        Start-Sleep -Milliseconds 2000
                        Await ($page.ScreenshotAsync([Microsoft.Playwright.PageScreenshotOptions]@{ Path = (Join-Path $shots "$($s.Name)_$($v.W).png") })) | Out-Null
                        Write-Host "[$side] $($s.Name) $($v.W)"
                    }
                    finally { Await ($ctx.CloseAsync()) | Out-Null }
                }
            }
        }
        finally {
            if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
            foreach ($name in 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'UseMockData', 'KeyVault__Uri', 'Storage__UseAzurite', 'Storage__AzuritePorts') {
                Remove-Item "Env:$name" -ErrorAction SilentlyContinue
            }
        }
        return $shots
    }

    $beforeShots = if ($BeforeDir) { $BeforeDir } else { Invoke-Shots (Get-SourceDir $Before 'before') 'before' }
    $afterShots = if ($AfterDir) { $AfterDir } else { Invoke-Shots (Get-SourceDir $After 'after') 'after' }

    function Find-Shot([string] $dir, $screen, [int] $w) {
        $slug = $screen.Route.Trim('/') -replace '/', '_'
        Get-ChildItem $dir -Filter '*.png' | Where-Object { $_.Name -eq "$($screen.Name)_$w.png" -or $_.Name -like "*-$($w)_$slug.png" } | Select-Object -First 1
    }

    # Pixel diff in the browser: changed 16 px cells are merged into boxes and drawn on the after shot.
    $diffJs = @'
async ([before, after]) => {
  const load = src => new Promise((ok, fail) => { const i = new Image(); i.onload = () => ok(i); i.onerror = fail; i.src = src; });
  const [a, b] = await Promise.all([load(before), load(after)]);
  const w = Math.max(a.width, b.width), h = Math.max(a.height, b.height);
  const read = img => { const c = new OffscreenCanvas(w, h), g = c.getContext('2d'); g.fillStyle = '#fff'; g.fillRect(0, 0, w, h); g.drawImage(img, 0, 0); return g.getImageData(0, 0, w, h).data; };
  const pa = read(a), pb = read(b), C = 16, cols = Math.ceil(w / C), rows = Math.ceil(h / C), hot = new Uint8Array(cols * rows);
  let changed = 0;
  for (let i = 0; i < pa.length; i += 4) {
    if (Math.abs(pa[i] - pb[i]) + Math.abs(pa[i + 1] - pb[i + 1]) + Math.abs(pa[i + 2] - pb[i + 2]) > 48) {
      changed++; const p = i / 4; hot[Math.floor(p / w / C) * cols + Math.floor((p % w) / C)] = 1;
    }
  }
  const seen = new Uint8Array(hot.length), boxes = [];
  for (let s = 0; s < hot.length; s++) {
    if (!hot[s] || seen[s]) continue;
    let x0 = cols, y0 = rows, x1 = 0, y1 = 0; const stack = [s]; seen[s] = 1;
    while (stack.length) {
      const c = stack.pop(), cx = c % cols, cy = Math.floor(c / cols);
      x0 = Math.min(x0, cx); y0 = Math.min(y0, cy); x1 = Math.max(x1, cx); y1 = Math.max(y1, cy);
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
        const nx = cx + dx, ny = cy + dy, n = ny * cols + nx;
        if (nx >= 0 && ny >= 0 && nx < cols && ny < rows && hot[n] && !seen[n]) { seen[n] = 1; stack.push(n); }
      }
    }
    boxes.push([x0 * C, y0 * C, (x1 + 1) * C, (y1 + 1) * C]);
  }
  const out = new OffscreenCanvas(b.width, b.height), g = out.getContext('2d');
  g.drawImage(b, 0, 0); g.strokeStyle = '#d00'; g.lineWidth = 3;
  for (const [l, t, r, d] of boxes) g.strokeRect(l - 2, t - 2, r - l + 4, d - t + 4);
  const blob = await out.convertToBlob({ type: 'image/png' });
  const marked = await new Promise(ok => { const f = new FileReader(); f.onload = () => ok(f.result); f.readAsDataURL(blob); });
  return JSON.stringify({ pct: Math.round(changed * 10000 / (w * h)) / 100, boxes: boxes.length, marked });
}
'@
    $tool = Await ($browser.NewPageAsync())
    $toUri = { param($path) 'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) }
    $rows = foreach ($s in $screenList) {
        foreach ($v in $viewports) {
            $a = Find-Shot $afterShots $s $v.W
            if (-not $a) { Write-Warning "no after shot for $($s.Name) at $($v.W)"; continue }
            $b = Find-Shot $beforeShots $s $v.W
            $aUri = & $toUri $a.FullName
            $bUri = if ($b) { & $toUri $b.FullName } else { $null }
            $diff = if ($bUri) { Await ($tool.EvaluateAsync[string]($diffJs, [string[]]@($bUri, $aUri))) | ConvertFrom-Json } else { $null }
            [pscustomobject]@{ Screen = $s; W = $v.W; H = $v.H; Before = $bUri; After = $aUri; Diff = $diff; Changed = (-not $diff) -or $diff.pct -gt 0 }
        }
    }

    # Keep the page small: every embedded shot is stored at the size the page shows it (half width on desktop,
    # 0.6 on phones), not at full resolution.
    $shrinkJs = @'
async ([src, scale]) => {
  const img = await new Promise((ok, fail) => { const i = new Image(); i.onload = () => ok(i); i.onerror = fail; i.src = src; });
  const c = new OffscreenCanvas(Math.round(img.width * scale), Math.round(img.height * scale)), g = c.getContext('2d');
  g.imageSmoothingQuality = 'high'; g.drawImage(img, 0, 0, c.width, c.height);
  const blob = await c.convertToBlob({ type: 'image/png' });
  return await new Promise(ok => { const f = new FileReader(); f.onload = () => ok(f.result); f.readAsDataURL(blob); });
}
'@
    foreach ($r in $rows) {
        $scale = if ($r.W -gt 800) { 0.5 } else { 0.6 }
        $shrink = { param($uri) Await ($tool.EvaluateAsync[string]($shrinkJs, [object[]]@($uri, $scale))) }
        if ($r.Before) { $r.Before = & $shrink $r.Before }
        $r.After = & $shrink $r.After
        if ($r.Diff) { $r.Diff.marked = & $shrink $r.Diff.marked }
    }

    $enc = [Net.WebUtility]
    $title = "$Date $Feature"
    $beforeLabel = if ($BeforeDir) { "PNGs from $BeforeDir" } else { "git $Before ($(git -C $root rev-parse --short $Before))" }
    $afterLabel = if ($AfterDir) { "PNGs from $AfterDir" } elseif ($After) { "git $After ($(git -C $root rev-parse --short $After))" } else { "working tree on $(git -C $root rev-parse --short HEAD)" }
    $css = 'body{font:14px system-ui,sans-serif;margin:16px;background:#fff;color:#111}h1{font-size:20px;margin:0 0 8px}' +
        'h2{font-size:15px;margin:18px 0 6px}.pair{display:flex;gap:12px;align-items:flex-start;flex-wrap:wrap}figure{margin:0}' +
        'figcaption{font-weight:600;margin-bottom:4px}img{border:1px solid #bbb;display:block}.same{color:#555}.chg{color:#b00}'
    function Format-Page([object[]] $items, [double] $wideScale, [double] $phoneScale, [bool] $showAll) {
        $sb = [Text.StringBuilder]::new()
        [void]$sb.Append("<!doctype html><html lang=`"en`"><head><meta charset=`"utf-8`"><meta name=`"viewport`" content=`"width=device-width`"><title>$($enc::HtmlEncode($title))</title><style>$css</style></head><body>")
        [void]$sb.Append("<h1>PoSeeReview $($enc::HtmlEncode($title)): before / after</h1>")
        [void]$sb.Append("<p>Before: $($enc::HtmlEncode($beforeLabel)). After: $($enc::HtmlEncode($afterLabel)). Local trimmed Release publish (Test env, mock data), 1366x768 and 390x844; red boxes mark changed pixels.</p>")
        if ($Notes) { [void]$sb.Append('<ul>' + (($Notes | ForEach-Object { "<li>$($enc::HtmlEncode($_))</li>" }) -join '') + '</ul>') }
        foreach ($r in $items) {
            $px = [int]($r.W * $(if ($r.W -gt 800) { $wideScale } else { $phoneScale }))
            $state = if (-not $r.Diff) { '<span class="chg">no before shot</span>' }
            elseif ($r.Changed) { "<span class=`"chg`">changed: $($r.Diff.pct)% of pixels, $($r.Diff.boxes) area(s)</span>" }
            else { '<span class="same">no visible change</span>' }
            $who = if ($r.Screen.SignedIn) { 'signed in' } else { 'signed out' }
            [void]$sb.Append("<h2>$($r.Screen.Name) ($($enc::HtmlEncode($r.Screen.Route)), $who) at $($r.W)x$($r.H): $state</h2><div class=`"pair`">")
            if ($r.Changed -or $showAll) {
                if ($r.Before) { [void]$sb.Append("<figure><figcaption>Before</figcaption><img width=`"$px`" alt=`"before`" src=`"$($r.Before)`"></figure>") }
                $shown = if ($r.Diff) { $r.Diff.marked } else { $r.After }
                [void]$sb.Append("<figure><figcaption>After</figcaption><img width=`"$px`" alt=`"after`" src=`"$shown`"></figure>")
            }
            else { [void]$sb.Append("<figure><figcaption>Before = after</figcaption><img width=`"$([int]($px / 2))`" alt=`"unchanged`" src=`"$($r.After)`"></figure>") }
            [void]$sb.Append('</div>')
        }
        [void]$sb.Append('</body></html>')
        $sb.ToString()
    }

    New-Item -ItemType Directory -Force $outDir | Out-Null
    $html = Join-Path $outDir "$Date-$Feature.html"
    $png = Join-Path $outDir "$Date-$Feature.png"
    [IO.File]::WriteAllText($html, (Format-Page $rows 0.5 0.6 $false), [Text.UTF8Encoding]::new($false))

    # The one PNG: the four most-changed screens, shrunk until it is under ~300 KB. The HTML has the rest.
    $top = @($rows | Where-Object Changed | Sort-Object { if ($_.Diff) { $_.Diff.pct } else { 100 } } -Descending | Select-Object -First 4)
    if (-not $top) { $top = @($rows | Select-Object -First 2) }
    $rest = @($rows | Where-Object Changed).Count - $top.Count
    if ($rest -gt 0) { $Notes += "This PNG shows the $($top.Count) most-changed screens; $rest more are in $Date-$Feature.html." }
    foreach ($scale in 0.45, 0.35, 0.28, 0.22) {
        $p = Await ($browser.NewPageAsync([Microsoft.Playwright.BrowserNewPageOptions]@{ ViewportSize = [Microsoft.Playwright.ViewportSize]@{ Width = 1300; Height = 800 } }))
        Await ($p.SetContentAsync((Format-Page $top $scale ($scale * 1.4) $true))) | Out-Null
        Await ($p.ScreenshotAsync([Microsoft.Playwright.PageScreenshotOptions]@{ Path = $png; FullPage = $true })) | Out-Null
        Await ($p.CloseAsync()) | Out-Null
        if ((Get-Item $png).Length -le 300KB) { break }
    }
    Write-Host ''
    Write-Host "Page: $html ($([int]((Get-Item $html).Length / 1KB)) KB)" -ForegroundColor Green
    Write-Host "PNG:  $png ($([int]((Get-Item $png).Length / 1KB)) KB)" -ForegroundColor Green
    $rows | ForEach-Object { '{0,-14} {1,4}  {2}' -f $_.Screen.Name, $_.W, $(if (-not $_.Diff) { 'no before' } elseif ($_.Changed) { "changed $($_.Diff.pct)%" } else { 'same' }) }
}
finally {
    if ($browser) { Await ($browser.CloseAsync()) | Out-Null }
    if ($playwright) { $playwright.Dispose() }
    foreach ($w in $worktrees) { git -C $root worktree remove --force $w 2>&1 | Out-Null }
    docker rm -f $container *> $null
    if (-not $KeepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
