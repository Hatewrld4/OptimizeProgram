# Optimize Program

Сборка exe на Windows с установленным .NET 8 SDK:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out

Готовый файл: out\OptimizeProgram.exe (запускается от администратора).

Без установки SDK: залей папку в GitHub-репозиторий, вкладка Actions → Build exe → Artifacts.
