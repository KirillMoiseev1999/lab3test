# Практическая работа 3

Проекты рассчитаны на .NET 8.

- `Lab3.Tests` — исходные классы и тесты подсчёта простых чисел и обработки заказов. Ревью BinaryMaxHeap приведено в отчёте.
- `Subscriptions.Api` — API сервиса учёта подписок на ASP.NET Core и SQLite.
- `Subscriptions.Tests` — юнит-тесты доменных правил API.
- `API-контракт.md` — маршруты, форматы данных и ожидаемые ответы.

Из корня репозитория можно запустить тесты обеих частей:

```powershell
dotnet test ./Lab3.Tests/Lab3.Tests.csproj
dotnet test ./Subscriptions.Tests/Subscriptions.Tests.csproj
```

Для локального запуска API:

```powershell
dotnet run --project ./Subscriptions.Api/Subscriptions.Api.csproj --launch-profile https
```

Swagger доступен по адресу `https://localhost:7028/swagger`. Приложение создаёт локальную базу при запуске. В режиме Development письма восстановления сохраняются в папке `dev-mail`.
