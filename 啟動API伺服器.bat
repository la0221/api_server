@echo off
chcp 65001 >nul
title AIVision API Server (port 5030)
echo ============================================
echo  AIVision 中央推論 API Server
echo  埠：http://localhost:5030
echo  驗活：http://localhost:5030/api/infer/health
echo  ※ 關掉這個視窗 = 關掉 server
echo ============================================
cd /d "d:\新增資料夾\VISION\AIVision\AIVision"
dotnet run --project "AIVision.Api\AIVision.Api.csproj" -c Release
pause
