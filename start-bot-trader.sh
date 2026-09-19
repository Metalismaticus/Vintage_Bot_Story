#!/usr/bin/env sh
# Роль продавца: обходит прилавки, торгует по табличкам, стоит на посту.
# Нужен магазин, размеченный табличками: !STORE (граница), !CASH + min N
# (касса), !ТОВАР + buy N / sell N (прилавок), !ТОВАР без цен (склад).
set -e
cd "$(dirname "$0")"
exec dotnet run --project VintageBotStory -- --trader --diag "$@"
