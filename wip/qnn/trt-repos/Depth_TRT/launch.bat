@echo off
:: Depth TRT - First-run launcher
:: MOTW immune - elevates once, unblocks everything, then opens setup.ps1 menu.

echo.
echo  Depth TRT
echo  Requesting elevation to unblock downloaded scripts...
echo.

powershell -ExecutionPolicy Bypass -Command ^
  "Start-Process pwsh -ArgumentList '-ExecutionPolicy Bypass -File ""%~dp0setup.ps1""' -Verb RunAs -Wait"

echo.
echo  Done. You can close this window.
pause
