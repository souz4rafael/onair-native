; onAIr Native — NSIS installer script
; Build with:  makensis onair-native.nsi
; Produces:    onAIr-Native-Setup-${VERSION}.exe  (self-contained, no prerequisites)

Unicode true
SetCompressor /SOLID lzma

;-------------------------------------------------------------
;  Product metadata
;-------------------------------------------------------------
!define PRODUCT_NAME        "onAIr Native"
!define PRODUCT_VERSION     "1.0.1"
!define PRODUCT_PUBLISHER   "Rafael Souza"
!define PRODUCT_WEB_SITE    "https://github.com/souz4rafael/onair-native"
!define PRODUCT_EXE         "OnAirNative.exe"
!define PRODUCT_DIRREGKEY   "Software\onAIr Native"
!define UNINST_REGKEY       "Software\Microsoft\Windows\CurrentVersion\Uninstall\onAIr Native"

; Folder produced by `dotnet publish ... -o ..\dist\publish-current`
!define PUBLISH_DIR         "..\dist\publish-current"

;-------------------------------------------------------------
;  Modern UI
;-------------------------------------------------------------
!include "MUI2.nsh"
!include "FileFunc.nsh"
!insertmacro GetSize

!define MUI_ABORTWARNING
!define MUI_ICON   "..\OnAirNative\Assets\app-icon.ico"
!define MUI_UNICON "..\OnAirNative\Assets\app-icon.ico"

; Offer to launch the app after install
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch onAIr Native"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

;-------------------------------------------------------------
;  Output
;-------------------------------------------------------------
Name        "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile     "onAIr-Native-Setup-${PRODUCT_VERSION}.exe"
InstallDir  "$PROGRAMFILES64\onAIr Native"
InstallDirRegKey HKLM "${PRODUCT_DIRREGKEY}" "InstallDir"
RequestExecutionLevel admin

;-------------------------------------------------------------
;  Version info on the setup .exe itself
;-------------------------------------------------------------
VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey "ProductName"     "${PRODUCT_NAME}"
VIAddVersionKey "FileVersion"     "${PRODUCT_VERSION}.0"
VIAddVersionKey "ProductVersion"  "${PRODUCT_VERSION}.0"
VIAddVersionKey "CompanyName"     "${PRODUCT_PUBLISHER}"
VIAddVersionKey "LegalCopyright"  "© ${PRODUCT_PUBLISHER}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} Setup"

;-------------------------------------------------------------
;  Install
;-------------------------------------------------------------
Section "onAIr Native (required)" SEC_MAIN
  SectionIn RO
  SetOutPath "$INSTDIR"
  SetOverwrite on

  ; Copy the entire self-contained publish output (recursively)
  File /r "${PUBLISH_DIR}\*.*"

  ; Start Menu + Desktop shortcuts
  CreateDirectory "$SMPROGRAMS\onAIr Native"
  CreateShortCut  "$SMPROGRAMS\onAIr Native\onAIr Native.lnk" "$INSTDIR\${PRODUCT_EXE}" "" "$INSTDIR\Assets\app-icon.ico"
  CreateShortCut  "$SMPROGRAMS\onAIr Native\Uninstall onAIr Native.lnk" "$INSTDIR\uninstall.exe"
  CreateShortCut  "$DESKTOP\onAIr Native.lnk" "$INSTDIR\${PRODUCT_EXE}" "" "$INSTDIR\Assets\app-icon.ico"

  ; Remember install location
  WriteRegStr HKLM "${PRODUCT_DIRREGKEY}" "InstallDir" "$INSTDIR"

  ; Add/Remove Programs entry
  WriteRegStr   HKLM "${UNINST_REGKEY}" "DisplayName"     "${PRODUCT_NAME}"
  WriteRegStr   HKLM "${UNINST_REGKEY}" "DisplayVersion"  "${PRODUCT_VERSION}"
  WriteRegStr   HKLM "${UNINST_REGKEY}" "Publisher"       "${PRODUCT_PUBLISHER}"
  WriteRegStr   HKLM "${UNINST_REGKEY}" "URLInfoAbout"    "${PRODUCT_WEB_SITE}"
  WriteRegStr   HKLM "${UNINST_REGKEY}" "DisplayIcon"     "$INSTDIR\${PRODUCT_EXE}"
  WriteRegStr   HKLM "${UNINST_REGKEY}" "UninstallString" "$INSTDIR\uninstall.exe"
  WriteRegStr   HKLM "${UNINST_REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "${UNINST_REGKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_REGKEY}" "NoRepair" 1

  ; Estimated size for Add/Remove Programs (KB)
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_REGKEY}" "EstimatedSize" "$0"

  ; Write the uninstaller
  WriteUninstaller "$INSTDIR\uninstall.exe"
SectionEnd

;-------------------------------------------------------------
;  Uninstall
;-------------------------------------------------------------
Section "Uninstall"
  ; Shortcuts
  Delete "$DESKTOP\onAIr Native.lnk"
  Delete "$SMPROGRAMS\onAIr Native\onAIr Native.lnk"
  Delete "$SMPROGRAMS\onAIr Native\Uninstall onAIr Native.lnk"
  RMDir  "$SMPROGRAMS\onAIr Native"

  ; Program files
  RMDir /r "$INSTDIR"

  ; Registry
  DeleteRegKey HKLM "${UNINST_REGKEY}"
  DeleteRegKey HKLM "${PRODUCT_DIRREGKEY}"
SectionEnd
