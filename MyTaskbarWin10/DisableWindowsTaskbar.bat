@echo off
REM Полное отключение системной панели Windows 10 и меню
REM Требует прав администратора

echo Отключение системной панели и меню Windows...

REM Завершаем процессы Explorer связанные с UI
taskkill /F /IM explorer.exe 2>nul
timeout /t 1 /nobreak

REM Отключаем автозагрузку explorer в Scheduled Tasks
schtasks /change /tn "Microsoft\Windows\WindowsBackup\ConfigNotification" /disable 2>nul

REM Отключаем Shell в реестре (если еще не отключена)
reg add "HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /d "" /f

REM Отключаем возможность использовать старый Shell (через Group Policy)
reg add "HKCU\Software\Policies\Microsoft\Windows\System" /v Shell /d "" /f

REM Скрываем системный трей
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ShowTaskBtnMn /t REG_DWORD /d 0 /f

REM Убедитесь что реестр обновлен
taskkill /F /IM explorer.exe 2>nul
timeout /t 1 /nobreak

echo.
echo ========================================================
echo Панель и меню отключены!
echo.
echo Если нужно восстановить штатную панель позже:
echo 1. Откройте Settings -> System -> Display
echo 2. Или переустановите MyTaskbar
echo ========================================================

pause
