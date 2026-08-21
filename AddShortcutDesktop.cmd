@rem SCO LIDEX - Windows desktop-shortcut installer.
@rem Copyright (C) Scott Brunner, Beast of Burden
@rem Part of the SCO LIDEX Terrain Builder application.
@rem Licensed under GNU GPL v3 or later. See LICENSE.txt.

@echo off
setlocal

set "ROOT=%~dp0"
set "APP=%ROOT%SCOLIDEX-win-x64\SCOLIDEX.exe"

if not exist "%APP%" (
    echo SCOLIDEX.exe was not found at:
    echo %APP%
    echo.
    echo Keep this shortcut helper in the top-level SCOLIDEX folder.
    pause
    exit /b 1
)

set "SCOLIDEX_APP=%APP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$target=$env:SCOLIDEX_APP; $shortcutPath=Join-Path ([Environment]::GetFolderPath('Desktop')) 'SCO LIDEX.lnk'; $ws=New-Object -ComObject WScript.Shell; $s=$ws.CreateShortcut($shortcutPath); $s.TargetPath=$target; $s.Arguments='--gui'; $s.WorkingDirectory=Split-Path -Parent $target; $s.IconLocation=$target; $s.Description='Launch SCO LIDEX terrain builder GUI'; $s.Save(); Write-Host 'Created desktop shortcut:' $shortcutPath"

echo.
pause
