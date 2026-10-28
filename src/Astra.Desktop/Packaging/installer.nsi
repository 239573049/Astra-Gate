; Windows installer for the Avalonia desktop app: Astra-<version>-<arch>-setup.exe.
;
; Built by scripts/package-desktop.mjs:
;   makensis /DSOURCE_DIR=<app dir> /DVERSION=<x.y.z> /DOUTFILE=<installer> /DICON=<Astra.ico> installer.nsi
;
; It replaces the Electron app's electron-builder installer in place (plan avalonia-client §5.3), so it keeps that
; installer's conventions: a per-user install into %LOCALAPPDATA%\Programs\Astra (or wherever the previous install's
; HKCU\Software\{APP_GUID} InstallLocation points), the same uninstall key (the GUID electron-builder derives from the
; app id io.astra.desktop), Astra.exe as the program, "Uninstall Astra.exe", the "Astra" Start menu and desktop
; shortcuts, and /S for a silent run (--force-run starts the app afterwards, as electron-updater asks). Installing
; over an Electron install removes the old program files first; the user's data (%APPDATA%\Astra, ~/.astra) stays.

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
RequestExecutionLevel user

!define PRODUCT_NAME "Astra"
!define APP_EXE "Astra.exe"
!define UNINSTALLER "Uninstall Astra.exe"
; UUID v5 of "io.astra.desktop" in electron-builder's namespace 50e065bc-3134-11e6-9bab-38c9862bdaf3.
!define APP_GUID "dc24f44e-a913-5500-9f16-0f671447e8c8"
!define INSTALL_REGISTRY_KEY "Software\${APP_GUID}"
!define UNINSTALL_REGISTRY_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_GUID}"

!include "MUI2.nsh"
!include "FileFunc.nsh"

Name "${PRODUCT_NAME}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT_NAME}"
InstallDirRegKey HKCU "${INSTALL_REGISTRY_KEY}" InstallLocation
BrandingText "${PRODUCT_NAME} ${VERSION}"

VIProductVersion "${FILE_VERSION}"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey "LegalCopyright" "AIDotNet"

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; Stop the app and the server it bundles before their files are replaced (a running exe cannot be overwritten).
; Only processes started from this install directory are touched: a server the CLI manages elsewhere keeps running.
!macro StopRunning
  nsExec::Exec `"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "Get-Process Astra,astra-server -ErrorAction SilentlyContinue | Where-Object { $$_.Path -like '$INSTDIR\*' } | Stop-Process -Force -ErrorAction SilentlyContinue"`
  Pop $0
  Sleep 500
!macroend

Function .onInit
  ; A UI language other than Chinese gets the English pages.
  StrCmp $LANGUAGE 2052 +2
  StrCpy $LANGUAGE 1033
FunctionEnd

Section "Install"
  !insertmacro StopRunning

  ; An earlier install (Electron or this one) in the same directory: its program files go, so nothing stale
  ; (resources\app.asar, old Chromium DLLs) is left behind. Only the directory the registry records as Astra's install
  ; is cleared — never one the user merely picked. User data is not under $INSTDIR.
  ReadRegStr $R0 HKCU "${INSTALL_REGISTRY_KEY}" InstallLocation
  StrCmp $R0 "$INSTDIR" 0 +3
  IfFileExists "$INSTDIR\${APP_EXE}" 0 +2
    RMDir /r "$INSTDIR"

  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*.*"
  WriteUninstaller "$INSTDIR\${UNINSTALLER}"

  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0

  WriteRegStr HKCU "${INSTALL_REGISTRY_KEY}" InstallLocation "$INSTDIR"
  WriteRegStr HKCU "${INSTALL_REGISTRY_KEY}" KeepShortcuts "true"
  WriteRegStr HKCU "${INSTALL_REGISTRY_KEY}" ShortcutName "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" DisplayName "${PRODUCT_NAME} ${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" UninstallString '"$INSTDIR\${UNINSTALLER}" /currentuser'
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" QuietUninstallString '"$INSTDIR\${UNINSTALLER}" /currentuser /S'
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" DisplayVersion "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" DisplayIcon "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" Publisher "AIDotNet"
  WriteRegStr HKCU "${UNINSTALL_REGISTRY_KEY}" InstallLocation "$INSTDIR"
  WriteRegDWORD HKCU "${UNINSTALL_REGISTRY_KEY}" NoModify 1
  WriteRegDWORD HKCU "${UNINSTALL_REGISTRY_KEY}" NoRepair 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTALL_REGISTRY_KEY}" EstimatedSize "$0"

  ; electron-updater's silent update asks for the app to be started again.
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "--force-run" $R1
  IfErrors +2
    Exec '"$INSTDIR\${APP_EXE}"'
SectionEnd

Section "Uninstall"
  !insertmacro StopRunning
  Delete "$SMPROGRAMS\${PRODUCT_NAME}.lnk"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTALL_REGISTRY_KEY}"
  DeleteRegKey HKCU "${INSTALL_REGISTRY_KEY}"
SectionEnd
