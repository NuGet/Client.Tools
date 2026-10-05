@echo off
call "%~dp0eng\common\build.cmd" -restore -build %*
exit /b %ErrorLevel%
