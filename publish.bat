@echo off
rem SOBRAT ODIN FAJL NA KAZHDUJU SISTEMU - chtoby bota mog zapustit chelovek,
rem kotoryj ne stavil ni .NET, ni ishodnikov. Podrobnosti - v publish.sh,
rem zdes to zhe samoe dlja Windows.
rem
rem PRO CHUZHIE SISTEMY: nichego dopolnitelno ukazyvat NE NUZHNO. Biblioteku
rem szhatija (libzstd) bot beret sam pri zapuske - iz igry, ustanovlennoj na
rem toj mashine, gde on rabotaet. Poetomu arhivy dlja Linux i macOS
rem sobirajutsja prjamo otsjuda, s Windows.
rem
rem POCHEMU ETOT FAJL LATINICEJ. cmd chitaet .bat v odnobajtovoj kodirovke
rem konsoli, a fajl lezhit v UTF-8: russkie bukvy prevrashhajutsja v musor,
rem i odna takaja stroka uzhe uronila razbor vsego fajla ("бот was unexpected
rem at this time"). Chelovecheskie objasnenija - v publish.sh i v ZAPUSK.md,
rem a zdes tolko to, chto dolzhno RABOTAT na ljuboj mashine.
rem
rem Imena peremennyh tozhe latinskie: v publish.sh "SISTEMY" po-russki lomalo
rem lyubuju obolochku ("command not found") - ta zhe beda, drugoj jazyk.
setlocal
cd /d "%~dp0"

set targets=%*
if "%targets%"=="" set targets=win-x64 linux-x64 osx-arm64 osx-x64

for %%s in (%targets%) do (
    echo === %%s ===
    dotnet publish VintageBotStory\VintageBotStory.csproj -c Release -r %%s --self-contained true ^
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
        -p:DebugType=none -o "out\%%s" --nologo || exit /b 1
)

echo.
echo Gotovo. Razdavat nuzhno VSJU papku out\^<sistema^>: rjadom s programmoj
echo lezhit papka presets - eto gotovye boty, kotorye vidno v okne upravlenija.
