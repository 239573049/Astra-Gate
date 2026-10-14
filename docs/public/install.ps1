# Astra installer for Windows (PowerShell 5.1+).
#
#   iwr https://astra-gate.si/install.ps1 -UseBasicParsing | iex
#
# `iex` cannot pass arguments, so choose the target with an environment variable first:
#
#   $env:ASTRA_INSTALL = 'server'; iwr https://astra-gate.si/install.ps1 -UseBasicParsing | iex
#   $env:ASTRA_INSTALL = 'desktop'; iwr https://astra-gate.si/install.ps1 -UseBasicParsing | iex
#
# Installs either
#   server   the self-contained astra-server.exe (+ web UI) into %USERPROFILE%\.astra\server and an
#            `astra-server` launcher on your user PATH. No Node.js or npm needed.
#   desktop  the desktop app (the NSIS installer, run silently, per-user). It bundles its own server.
#
# Without ASTRA_INSTALL it asks and defaults to the server.
#
# Environment:
#   ASTRA_INSTALL=server|desktop
#   ASTRA_HOME            data directory (default %USERPROFILE%\.astra)
#   ASTRA_FEED_URL        release feed (default https://astra-gate.si/api/client-releases)
#   ASTRA_NPM_REGISTRY    where the server tarball comes from (default https://registry.npmjs.org)
#
# Every download is verified against a sha256 published in the release feed before it is installed.

function Install-Astra {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

    $feed = if ($env:ASTRA_FEED_URL) { $env:ASTRA_FEED_URL.TrimEnd('/') } else { 'https://astra-gate.si/api/client-releases' }
    $registry = if ($env:ASTRA_NPM_REGISTRY) { $env:ASTRA_NPM_REGISTRY.TrimEnd('/') } else { 'https://registry.npmjs.org' }
    $channel = 'stable'
    $astraHome = if ($env:ASTRA_HOME) { $env:ASTRA_HOME } else { Join-Path $env:USERPROFILE '.astra' }

    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    switch ($arch) {
        'AMD64' { $cpu = 'x64' }
        'ARM64' { $cpu = 'arm64' }
        default { throw "Unsupported CPU '$arch'." }
    }
    $platform = "win32-$cpu"
    $rid = "win-$cpu"

    $mode = $env:ASTRA_INSTALL
    if (-not $mode) {
        Write-Host ''
        Write-Host 'What do you want to install?'
        Write-Host '  1) Server   - astra-server.exe + web console (no Node.js needed)'
        Write-Host '  2) Desktop  - the desktop app (includes the server)'
        $answer = ''
        try { $answer = Read-Host 'Choose [1/2] (default 1)' } catch { }
        switch ($answer.Trim().ToLower()) {
            { $_ -in '', '1', 's', 'server' } { $mode = 'server' }
            { $_ -in '2', 'd', 'desktop' } { $mode = 'desktop' }
            default { throw "Invalid choice '$answer'." }
        }
    }
    if ($mode -notin 'server', 'desktop') { throw "Unknown install target '$mode' (use server or desktop)." }

    function Get-Sha256($file) { (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLower() }
    function Assert-Sha256($file, $expected) {
        $actual = Get-Sha256 $file
        if ($actual -ne $expected.ToLower()) {
            throw "Checksum mismatch for $(Split-Path -Leaf $file) (expected $expected, got $actual). Nothing was installed."
        }
    }
    function Get-Json($url) { Invoke-RestMethod -UseBasicParsing -Uri $url }
    function Save-File($url, $dest) { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $dest }

    $manifest = Get-Json "$feed/$channel/latest.json"
    $version = [string]$manifest.version
    if ($version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$') { throw 'The release feed returned no usable version.' }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("astra-install-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        if ($mode -eq 'server') {
            $entry = $manifest.platforms.$rid
            if (-not $entry) { throw "No server build for $platform in the $version release." }
            $package = [string]$entry.server
            $sha = [string]$entry.serverSha256
            if ($package -notmatch '^@aidotnet/server-[a-z0-9-]+$') { throw "The release feed named an unexpected package '$package'." }
            if (-not $sha) { throw "The release feed has no checksum for the $platform server." }

            Write-Host "Installing Astra server $version ($platform)..."
            $short = $package.Substring('@aidotnet/'.Length)
            $tgz = Join-Path $tmp 'server.tgz'
            Save-File "$registry/$package/-/$short-$version.tgz" $tgz
            $pkgDir = Join-Path $tmp 'pkg'
            New-Item -ItemType Directory -Path $pkgDir | Out-Null
            & tar.exe -xzf $tgz -C $pkgDir
            if ($LASTEXITCODE -ne 0) { throw 'Could not unpack the server package (tar.exe is built into Windows 10 1803+).' }
            $bin = Join-Path $pkgDir 'package\bin'
            $exe = Join-Path $bin 'astra-server.exe'
            if (-not (Test-Path $exe)) { throw 'The downloaded package has no server binary.' }
            Write-Host 'Verifying checksum...'
            Assert-Sha256 $exe $sha

            $serverDir = Join-Path $astraHome 'server'
            New-Item -ItemType Directory -Force -Path $serverDir | Out-Null
            $target = Join-Path $serverDir "astra-server-$version.exe"
            Copy-Item -LiteralPath $exe -Destination "$target.tmp" -Force
            # The server loads SQLite and serves the UI from the directory of its own executable.
            Get-ChildItem -LiteralPath $bin -Filter '*.dll' -File | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $serverDir $_.Name) -Force
            }
            $www = Join-Path $bin 'wwwroot'
            if (Test-Path $www) {
                $wwwDest = Join-Path $serverDir 'wwwroot'
                $wwwNew = "$wwwDest.new"
                if (Test-Path $wwwNew) { Remove-Item -Recurse -Force $wwwNew }
                Copy-Item -Recurse -LiteralPath $www -Destination $wwwNew
                if (Test-Path $wwwDest) { Remove-Item -Recurse -Force $wwwDest }
                Move-Item -LiteralPath $wwwNew -Destination $wwwDest
            } else {
                Write-Warning 'The package ships no web UI; the console will not be served.'
            }
            Move-Item -LiteralPath "$target.tmp" -Destination $target -Force

            # install.json is shared with the CLI and desktop app: keep the fields we do not own.
            $installFile = Join-Path $astraHome 'install.json'
            $info = [ordered]@{}
            if (Test-Path $installFile) {
                try {
                    $old = Get-Content -Raw -LiteralPath $installFile | ConvertFrom-Json
                    foreach ($k in 'desktopPath', 'desktopVersion') { if ($old.$k) { $info[$k] = [string]$old.$k } }
                } catch { }
            }
            $info['serverPath'] = $target
            $info['serverVersion'] = $version
            $info['updatedAt'] = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            $info | ConvertTo-Json | Set-Content -LiteralPath $installFile -Encoding UTF8

            # A fixed launcher on the user PATH; re-running this installer refreshes it.
            $binDir = Join-Path $astraHome 'bin'
            New-Item -ItemType Directory -Force -Path $binDir | Out-Null
            Set-Content -LiteralPath (Join-Path $binDir 'astra-server.cmd') -Encoding ASCII -Value "@echo off`r`n`"$target`" %*"
            $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
            if (-not (($userPath -split ';') -contains $binDir)) {
                [Environment]::SetEnvironmentVariable('Path', ($(if ($userPath) { "$userPath;" } else { '' }) + $binDir), 'User')
                $env:Path = "$env:Path;$binDir"
                Write-Host "Added $binDir to your user PATH (open a new terminal to pick it up)."
            }

            Write-Host ''
            Write-Host "Astra server $version installed."
            Write-Host "  binary    $target"
            Write-Host "  launcher  $(Join-Path $binDir 'astra-server.cmd')"
            Write-Host ''
            Write-Host 'Start it with:  astra-server serve'
            Write-Host 'Then open the URL it prints (default http://127.0.0.1:<port>).'
            Write-Host 'To update later, run this installer again.'
            if (Test-Path (Join-Path $astraHome 'runtime.json')) {
                Write-Host ''
                Write-Host 'If Astra is already running, restart it to use the new version.'
            }
        } else {
            $name = "Astra-$version-$platform.exe"
            Write-Host "Installing Astra desktop $version ($platform)..."
            $releases = Get-Json "$feed/$channel/releases.json"
            $file = $null
            foreach ($v in $releases.versions) {
                if ([string]$v.version -ne $version) { continue }
                $file = $v.files | Where-Object { $_.name -eq $name } | Select-Object -First 1
            }
            if (-not $file) { throw "The $version release has no desktop build for $platform ($name)." }
            if (-not $file.sha256) { throw "The release feed has no checksum for $name." }

            $installer = Join-Path $tmp $name
            Save-File "$feed/$channel/v/$version/$name" $installer
            Write-Host 'Verifying checksum...'
            Assert-Sha256 $installer ([string]$file.sha256)

            Write-Host 'Running the installer (per-user, silent)...'
            $p = Start-Process -FilePath $installer -ArgumentList '/S' -Wait -PassThru
            if ($p.ExitCode -ne 0) { throw "The installer exited with code $($p.ExitCode)." }
            Write-Host ''
            Write-Host "Astra desktop $version installed. Start it from the Start menu."
        }
    } finally {
        Remove-Item -Recurse -Force -LiteralPath $tmp -ErrorAction SilentlyContinue
    }
}

# Not `exit`: under `iex` that would close the user's PowerShell window.
try { Install-Astra } catch { Write-Host "error: $($_.Exception.Message)" -ForegroundColor Red }
