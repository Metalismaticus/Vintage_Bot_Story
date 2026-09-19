@echo off
title Vintage Bot Story — продавец
rem Роль продавца: обходит прилавки, торгует по табличкам, стоит на посту.
rem Нужен магазин, размеченный табличками: !STORE (граница), !CASH + min N
rem (касса), !ТОВАР + buy N / sell N (прилавок), !ТОВАР без цен (склад).
chcp 65001 >nul
dotnet run --project "%~dp0VintageBotStory" -- --trader --diag
pause
