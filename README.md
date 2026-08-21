# LernType

**LernType** — автономный настольный тренажёр немецкого языка для Windows 10/11 и русскоязычных пользователей. Внутренний маршрут начинается с подготовительной ступени **Pre-A1** и продолжается официальными уровнями CEFR **A1–C2**.

> LernType не связан с Goethe-Institut, telc, TestDaF-Institut или BAMF. Экзаменационный центр содержит справочные схемы и ссылки на первичные источники, а не официальные задания или официальный прогноз результата.

## Что реализовано

- **Раздельная сложность:** слова, предложения и тексты; отдельно выбираются уровень и направление RU→DE, DE→RU или смешанное.
- **Путь Pre-A1–C2:** цели по навыкам, канонические события попыток, пересчитываемое освоение и версионированное интервальное повторение `fsrs-like-v1`.
- **Двусторонняя диагностика словаря:** уникальные вопросы в обе стороны, точность по направлениям и разбор ошибок.
- **Личная библиотека:** вставка русского или немецкого текста до 500 000 символов, частотная лексика и тренировка выбранных слов. Текст остаётся временным, пока пользователь явно не сохранит проект.
- **Офлайн-словарь:** FreeDict/WikDict `rus-deu` и `deu-rus` версии 2025.11.23 в индексированной SQLite-базе.
- **Локальная аудиопрактика:** немецкий Windows TTS для прослушивания и диктанта, запись и воспроизведение ответа через микрофон. Устная часть оценивается пользователем самостоятельно; распознавания речи и автоматической оценки произношения нет.
- **Экзаменационный центр:** справочные маршруты Goethe, telc, digital TestDaF и DTZ с секциями, временем, правилами оценки, датой проверки и официальными ссылками.
- **Грамматика:** локальные учебные эвристики для основных конструкций; это подсказки, а не сертификационная оценка.
- **Опциональный онлайн-разбор:** OpenAI Responses API включается только явным действием и ограничивает объём запроса/ответа. Основные режимы работают без сети и API-ключа.
- **Интерфейс:** адаптивная тёплая glass-тема, полупрозрачные плитки, светлый/тёмный режим, Mica в Windows 11 и резервное оформление для Windows 10.

Встроенный каталог сейчас содержит 150 слов, 70 двуязычных предложений, 7 текстов и 8 грамматических заданий. Границы покрытия описаны ниже и не скрываются за общей меткой уровня.

## Скачать и запустить

В GitHub Releases используются автономные архивы:

- `LernType-<version>-win-x64.zip` — обычные 64-битные ПК Windows;
- `LernType-<version>-win-arm64.zip` — устройства Windows on Arm.

1. Скачайте архив и одноимённый файл `.sha256`.
2. Сверьте SHA-256, распакуйте архив и запустите `LernType.exe`.
3. Для автопереключения ввода добавьте в Windows раскладки `ru-RU` и `de-DE`.

MSIX/App Installer подходит для пользовательской установки только тогда, когда конкретный релиз явно помечен как подписанный и цифровая подпись успешно проверяется. CI также создаёт короткоживущий **unsigned validation MSIX** для проверки структуры; он не является стабильным установщиком.

## Сборка из исходников

Требуются Windows 10/11 и **.NET 10 SDK**. Версия SDK закреплена в `global.json`, зависимости — в `packages.lock.json`. Скрипты упаковки запускаются в PowerShell 7 (`pwsh`).

```powershell
dotnet tool restore
dotnet restore LernType.sln --locked-mode
dotnet format LernType.sln --verify-no-changes --no-restore
dotnet build LernType.sln -c Release --no-restore -warnaserror
dotnet test LernType.sln -c Release --no-build --no-restore
dotnet run --project src/WortBruecke.App/WortBruecke.App.csproj
```

Автономная публикация обеих архитектур:

```powershell
dotnet restore src/WortBruecke.App/WortBruecke.App.csproj --locked-mode
dotnet publish src/WortBruecke.App/WortBruecke.App.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o artifacts/publish/win-x64

dotnet publish src/WortBruecke.App/WortBruecke.App.csproj `
  -c Release -r win-arm64 --self-contained true --no-restore `
  -o artifacts/publish/win-arm64
```

Верификация MSIX-манифеста без публикации неподписанного пакета:

```powershell
./tools/Build-Msix.ps1 `
  -PublishDirectory artifacts/publish/win-x64 `
  -Architecture x64 -AllowUnsigned
```

Подписанный MSIX требует PFX с закрытым ключом и `Publisher`, точно совпадающий с subject сертификата:

```powershell
$password = Read-Host 'PFX password' -AsSecureString
./tools/Build-Msix.ps1 `
  -PublishDirectory artifacts/publish/win-x64 `
  -Architecture x64 `
  -CertificatePath C:\secure\LernType-signing.pfx `
  -CertificatePassword $password `
  -TimestampUri 'https://timestamp.example.org'
```

App Installer создаётся только для уже подписанного и размещённого по HTTPS MSIX. Полная процедура — в [docs/RELEASING.md](docs/RELEASING.md).

## Локальные данные и безопасное обновление

Профиль распакованной версии находится в `%LOCALAPPDATA%\LernType`:

- `lerntype.db` — контент, канонические попытки, состояния повторения, прогресс и явно сохранённые книги;
- `settings.json` — локальные настройки и защищённый для текущего пользователя API-ключ;
- `Backups` и `migration-journal.jsonl` — управляемые копии и журнал восстановления.

При обнаружении старого `%LOCALAPPDATA%\WortBruecke` мигратор учитывает SQLite WAL, выполняет checkpoint/SQLite Backup API, `quick_check` и сверку инвентаря, затем атомарно продвигает проверенную копию. Исходник и предмиграционная копия сохраняются, а журнал позволяет повторить или откатить прерванный перенос. Два непустых профиля не объединяются молча.

Удаление сохранённой книги очищает её активные строки и связанные записи из управляемых резервных копий. Временный вставленный текст и аудиозаписи не сохраняются по умолчанию.

## Контент и словарь

- каталог: `src/WortBruecke.App/Content/catalog.json`;
- экзаменационные схемы: `src/WortBruecke.App/Content/exams.json`;
- словарь и лицензии: `src/WortBruecke.App/Assets/Dictionary/FreeDict`.

Конвертер FreeDict TEI P5:

```powershell
dotnet run --project tools/WortBruecke.DictionaryBuilder -- `
  output.sqlite rus-deu.tei deu-rus.tei
```

Формат подписанных офлайн-контент-пакетов проверяет RSA-PSS подпись манифеста, SHA-256 каждого файла, совместимость приложения, лимиты размера и защиту от path traversal. Публикационный процесс и требования к рецензированию описаны в [docs/CONTENT-GOVERNANCE.md](docs/CONTENT-GOVERNANCE.md).

## Архитектура и проверка качества

- `WortBruecke.Core` — учебные модели, канонические события попыток, оценивание, освоение и интервальное повторение;
- `WortBruecke.Infrastructure` — SQLite, WAL-aware миграция, офлайн-словарь, аудио, защищённые настройки и сетевой клиент;
- `WortBruecke.App` — WPF/MVVM, DI/Generic Host, навигация, темы и учебные сценарии;
- `WortBruecke.Tests` — unit-, integration-, persistence-, resilience-, privacy- и content-тесты.

CI выполняет locked restore, форматирование, Debug/Release build с warnings-as-errors, тесты и порог покрытия, транзитивный vulnerability audit, CycloneDX SBOM, CodeQL и self-contained publish для x64/arm64. Все сторонние Actions закреплены полными commit SHA.

## Проверенная матрица релиза

Ниже зафиксирована локальная инженерная проверка промежуточного кандидата от **21 августа 2026 года**. Среда: Windows 11 Pro 25H2, build `26200.9168`, x64; .NET SDK `10.0.302`. Это не подмена итогового release record: после изменения исходников команды повторяются на точном публикуемом commit и его артефактах по [регламенту](docs/RELEASING.md).

Проверки зависимостей и состава выполнялись так:

```powershell
./.tools/dotnet10/dotnet.exe tool restore
./.tools/dotnet10/dotnet.exe restore LernType.sln --locked-mode
./.tools/dotnet10/dotnet.exe list LernType.sln package `
  --vulnerable --include-transitive --format json
./.tools/dotnet10/dotnet.exe CycloneDX src/WortBruecke.App/WortBruecke.App.csproj `
  --exclude-dev --disable-package-restore `
  --output artifacts/sbom --filename LernType.cdx.json --output-format Json `
  --set-name LernType --set-version 1.0.0 --set-type Application
```

Литеральный результат: все четыре команды завершились с кодом `0`; locked restore обработал 5 проектов; vulnerability audit обнаружил `0` уязвимых пакетов; CycloneDX `1.7` содержит 43 компонента и `0` компонентов без license metadata.

Runtime-smoke выполнялся только для x64-кандидата на указанной Windows 11 машине:

```powershell
$publish = 'artifacts/release-candidate-1.0.0/win-x64-r2r'
./tools/Invoke-ReleaseSmoke.ps1 -PublishDirectory $publish `
  -OutputDirectory artifacts/verification/release-evidence-wide `
  -WindowWidth 1180 -WindowHeight 760
./tools/Invoke-ReleaseSmoke.ps1 -PublishDirectory $publish `
  -OutputDirectory artifacts/verification/release-evidence-compact `
  -WindowWidth 820 -WindowHeight 600
./tools/Invoke-ReleaseSmoke.ps1 -PublishDirectory $publish `
  -OutputDirectory artifacts/verification/release-evidence-minimum `
  -WindowWidth 720 -WindowHeight 520
```

| Размер окна | Определённый layout | Метка `Путь Pre-A1–C2` | Главный landmark | Shell error / technical code | Screenshot | Закрытие |
|---|---|---:|---:|---:|---:|---:|
| `1180×760` | `Wide` | видима | да | нет / нет | да | exit `0` |
| `820×600` | `Compact` | скрыта | да | нет / нет | да | exit `0` |
| `720×520` | `Compact` | скрыта | да | нет / нет | да | exit `0` |

Все три JSON-записи имеют `schemaVersion: 2`, `layoutVerificationPassed: true` и `uiVerificationPassed: true`. Негативный contract self-test также проверен: видимый `Код: …` и заведомо отсутствующий landmark дали process exit `1`, `uiVerificationPassed: false`, сохранили screenshot и точную причину отказа. Для проверки перехода скрипт принимает `-InvokeAutomationName` вместе с обязательным для такого перехода `-ExpectedAutomationName`; успех требует ожидаемый видимый landmark и отсутствие `Ошибка приложения` либо видимого технического кода.

x64 был опубликован как self-contained/R2R-кандидат, Arm64 — как self-contained кандидат; release-конфигурация и CI теперь требуют R2R без trimming для обеих архитектур. PE-заголовки `LernType.exe` проверены: x64 — `0x8664`, Arm64 — `0xAA64`. **Arm64 в этой записи проверен только сборкой, PE-валидацией и MSIX `-WhatIf`; native runtime-smoke на Arm64-устройстве не выполнялся. Windows 10 22H2 остаётся best-effort целью и в этой записи runtime не проверялась.** Стабильный Arm64-релиз требует отдельной нативной проверки, а итоговый release record должен ссылаться на результаты, полученные уже из неизменённого релизного commit.

## Границы версии 1.0

- Встроенная словарная плотность выше на Pre-A1–A2; B1–C2 сильнее опираются на предложения, тексты и грамматику.
- Локальная устная практика записывает и воспроизводит речь, но не выполняет фонетическое распознавание; результат такой попытки помечается как самооценка.
- Книжный режим использует точные словоформы без полной морфологической лемматизации.
- Экзаменационные форматы меняются. До выполнения критериев из регламента публикации они показываются как справочные, а не как полноценные официальные пробные экзамены.
- Windows 11 является основной целью; Windows 10 22H2 поддерживается для ZIP-сборки в режиме best effort и не считается runtime-проверенной без отдельной записи на реальной системе.

## Лицензии и политика проекта

- Код LernType: [MIT](LICENSE).
- Словарь: [FreeDict](https://freedict.org/downloads/) / CC BY-SA 3.0; точные сведения и хэши находятся рядом с базой.
- [Privacy](PRIVACY.md) · [Security](SECURITY.md) · [Support](SUPPORT.md) · [Changelog](CHANGELOG.md)
