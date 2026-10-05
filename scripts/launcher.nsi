Unicode true
Name "Skyrim Version Patcher"
Caption "Skyrim Version Patcher"
OutFile "${OUTPUT_FILE}"
Icon "${ICON_FILE}"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "Skyrim Version Patcher"
VIAddVersionKey /LANG=1033 "FileDescription" "Skyrim Version Patcher"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" ""
RequestExecutionLevel user
SilentInstall silent
; Non-solid compression avoids a second, temporary copy of the expanded payload.
SetCompressor lzma

Section
    StrCmp $LOCALAPPDATA "" unpack_error
    StrCpy $INSTDIR "$LOCALAPPDATA\SkyrimVersionPatcher\App\${PAYLOAD_ID}"
    ClearErrors
    SetOutPath "$INSTDIR"
    IfErrors unpack_error
    StrCpy $1 0

    ; FileOpen permits readers, but excludes a second writer during extraction.
    lock:
    ClearErrors
    FileOpen $0 "$INSTDIR\.extract.lock" w
    IfErrors wait_for_lock locked
    wait_for_lock:
    IntOp $1 $1 + 1
    IntCmp $1 150 unpack_error 0 unpack_error
    Sleep 200
    Goto lock

    locked:
    ClearErrors
    ; Repair missing or changed files while skipping unchanged, possibly loaded DLLs.
    SetOverwrite ifdiff
    File /r "${PAYLOAD_DIR}\*"
    IfErrors unpack_error

    FileClose $0
    ClearErrors
    SetOutPath "$INSTDIR"
    ExecWait '"$INSTDIR\SkyrimVersionPatcher.exe" --legacy-data-directory "$EXEDIR"' $2
    IfErrors launch_error
    SetErrorLevel $2
    Quit

    unpack_error:
    FileClose $0
    MessageBox MB_OK|MB_ICONSTOP "Could not prepare Skyrim Version Patcher in LocalAppData.$\r$\nCheck free disk space and folder permissions, then try again.$\r$\n$INSTDIR"
    SetErrorLevel 1
    Quit

    launch_error:
    MessageBox MB_OK|MB_ICONSTOP "Could not start Skyrim Version Patcher.$\r$\n$INSTDIR\SkyrimVersionPatcher.exe"
    SetErrorLevel 1
    Quit
SectionEnd
