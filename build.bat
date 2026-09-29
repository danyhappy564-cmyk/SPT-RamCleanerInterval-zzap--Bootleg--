@echo off
rem Release build -> copies the DLL to <SPT>\BepInEx\plugins\CactusPie.RamCleanerInterval and writes release\*.zip
rem SPT folder defaults to E:\SPT 4.1 (change it in src\RamCleaner.local.props, see the .example file)
cd /d "%~dp0src"
dotnet build CactusPie.RamCleanerInterval.sln -c Release
pause
