@echo off
rem Double-click shim for launcher.ps1 - same trick as 1_EINRICHTEN.bat.
rem
rem  start "" /b ... -WindowStyle Hidden   so that no black console window sits
rem                                        behind the launcher for the rest of
rem                                        the session
rem  -STA                                  WinForms needs a single-threaded
rem                                        apartment
rem  Unblock-File                          Windows marks every file that came
rem                                        out of a downloaded zip, and a
rem                                        marked .ps1 will not run
cd /d "%~dp0"
start "" /b powershell -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -Command ^
  "Get-ChildItem '%~dp0*.ps1' | Unblock-File -ErrorAction SilentlyContinue; & '%~dp0launcher.ps1'"
exit
