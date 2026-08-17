# 1Cpt:Enterprise Debug Adapter

Адаптер отладки 1С:Предприятие (DAP) для HTTP‑сервера отладки 1С.

Этот репозиторий — **форк проекта [akpaevj/onec-debug-adapter](https://github.com/akpaevj/onec-debug-adapter)**
с доработками и упрощением под сценарий использования в расширении VS Code
[`yellow-hammer/vscode-1c-platform-tools`](https://github.com/yellow-hammer/vscode-1c-platform-tools).
Часть функциональности исходного проекта удалена, конфигурация адаптера и процесс релизов адаптированы под это расширение.

## Назначение

- Реализация протокола Debug Adapter Protocol (DAP) для HTTP‑сервера отладки 1С.
- Отладка исходников конфигураций (формат конфигуратора) и расширений.
- Основной потребитель — расширение VS Code **1C: Platform tools**; как отдельный DAP может использоваться продвинутыми пользователями при желании.

## Требования

- Для сборок из релиза под win-x64, linux-x64, osx-x64 и osx-arm64 ничего ставить не нужно: рантайм .NET лежит внутри архива. На Linux рантайму нужен системный пакет `libicu`, в большинстве дистрибутивов он уже стоит.
- Для универсального архива и сборки из исходников нужен [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0): Runtime и SDK соответственно.

## Сборка и запуск локально

```bash
dotnet publish onec-debug-adapter.csproj -c Release -o out/onec-debug-adapter
```

В результате в `out/onec-debug-adapter` будут лежать:

- `OnecDebugAdapter.dll`
- сопутствующие библиотеки.

Запуск DAP‑процесса вручную (пример):

```bash
dotnet OnecDebugAdapter.dll
```

Обычно запуск и параметры адаптера (аргументы, рабочий каталог) полностью
контролируются клиентом (например, VS Code через расширение `vscode-1c-platform-tools`).

## Параметры конфигурации (DAP launch/attach)

Основные параметры, которые передаёт клиент адаптеру (через DAP‑запросы, а не как аргументы командной строки):

- `rootProject` — путь к выгруженной конфигурации (каталог с исходниками конфигуратора).
- `platformPath` — путь к каталогу с установленными версиями платформы 1С.
- `platformVersion` — версия платформы.
- `debugServerHost` / `debugServerPort` — адрес HTTP‑сервера отладки 1С.
- `extensions` — массив путей к выгрузкам расширений конфигурации.
- `autoAttachTypes` — список типов подключаемых клиентов (Client, ManagedClient, Server и т.п.).
- `externalFilesSrc` / `externalFilesBuilds` — каталоги исходников и собранных файлов внешних обработок и отчётов.
- `user` / `password` — автовход в предприятие.
- `trace` — диагностический лог адаптера (события AdapterLog).

Полное пользовательское руководство со всеми параметрами и сценариями —
[docs/debug.md расширения vscode-1c-platform-tools](https://github.com/yellow-hammer/vscode-1c-platform-tools/blob/main/docs/debug.md).
