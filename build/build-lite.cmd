@echo off
setlocal EnableExtensions
title Nasaq - Build installer (no FFmpeg)
cd /d "%~dp0"

echo.
echo   Building the smaller Nasaq installer without bundled FFmpeg.
echo   Image and document conversion will work out of the box;
echo   audio and video will need FFmpeg installed separately.
echo.

call "%~dp0build.cmd" -NoFfmpeg
exit /b %ERRORLEVEL%
