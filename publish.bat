@echo off
chcp 65001 >nul
rem MoTask.exe と MoTask.Mcp.exe を同じフォルダ（既定は publish\）へ発行する。
rem 使い方: publish.bat [出力先フォルダ]
setlocal

set "ROOT=%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%ROOT%publish"

rem 起動中の exe は上書きできず発行が途中で失敗するので、先に止めてもらう。
tasklist /fi "imagename eq MoTask.exe" 2>nul | find /i "MoTask.exe" >nul
if not errorlevel 1 (
    echo MoTask.exe が起動中です。終了してから再実行してください。
    exit /b 1
)

echo [1/2] MoTask.App を発行しています...
dotnet publish "%ROOT%src\MoTask.App" -c Release -o "%OUT%"
if errorlevel 1 goto :fail

echo [2/2] MoTask.Mcp を発行しています...
dotnet publish "%ROOT%src\MoTask.Mcp" -c Release -o "%OUT%"
if errorlevel 1 goto :fail

echo.
echo 発行しました: %OUT%
exit /b 0

:fail
echo.
echo 発行に失敗しました。
exit /b 1
