using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Launcher.App.Services;

namespace Launcher.App;

public static class ErrorClassifier
{
    public static ErrorInfo Classify(Exception exception)
    {
        if (exception is HttpRequestException httpException)
        {
            if (IsDnsError(httpException))
            {
                return Create(
                    "Сайт лаунчера недоступен",
                    "Компьютер не смог найти bl-modern.ru. Обычно это временная проблема интернета, DNS или блокировка провайдера/антивируса.",
                    [
                        "Проверь интернет и открой https://bl-modern.ru в браузере.",
                        "Если сайт не открывается, попробуй другой DNS или VPN.",
                        "Нажми «Играть» ещё раз после восстановления соединения."
                    ],
                    exception);
            }

            if (httpException.StatusCode == HttpStatusCode.NotFound)
            {
                return Create(
                    "Файл не найден на сервере",
                    "Лаунчер обратился к серверу, но нужного файла там нет. Чаще всего это неправильная ссылка в манифесте или файл ещё не загружен на сайт.",
                    [
                        "Проверь ссылки на манифест, архив сборки и Forge installer.",
                        "Убедись, что файл доступен в браузере.",
                        "Если ошибка у игрока, пришли этот лог администратору."
                    ],
                    exception);
            }

            if (httpException.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                return Create(
                    "Сервер запретил скачивание",
                    "Сайт ответил отказом доступа. Возможно, файл закрыт правами, заблокирован hotlink-защитой или сервер не отдаёт его этому клиенту.",
                    [
                        "Проверь права файла на сервере.",
                        "Открой ссылку из манифеста в браузере без авторизации.",
                        "Если используется защита сайта, добавь исключение для лаунчера."
                    ],
                    exception);
            }

            return Create(
                "Ошибка сети",
                "Не удалось скачать данные с сервера лаунчера. Это может быть сбой интернета, сервера или временная ошибка маршрута.",
                [
                    "Проверь интернет.",
                    "Попробуй ещё раз через несколько минут.",
                    "Если повторяется, отправь лог администратору."
                ],
                exception);
        }

        if (exception is TaskCanceledException)
        {
            return Create(
                "Сервер долго не отвечает",
                "Операция заняла слишком много времени. Обычно это медленное соединение, перегруженный сервер или зависшее скачивание.",
                [
                    "Проверь скорость интернета.",
                    "Попробуй запустить установку ещё раз.",
                    "Если скачивание архива оборвалось, лаунчер продолжит его с места остановки."
                ],
                exception);
        }

        if (exception is UnauthorizedAccessException)
        {
            return Create(
                "Нет доступа к папке",
                "Windows не дала лаунчеру записать или заменить файл в папке установки.",
                [
                    "Закрой Minecraft и другие лаунчеры.",
                    "Выбери папку установки, куда у пользователя есть права записи.",
                    "Проверь, не блокирует ли файл антивирус."
                ],
                exception);
        }

        if (exception is IOException ioException)
        {
            if (IsNetworkIoError(ioException))
            {
                return Create(
                    "Ошибка сети",
                    "Соединение с сервером оборвалось во время загрузки. Обычно это нестабильный интернет, VPN/Zapret, прокси или HTTPS-фильтр антивируса.",
                    [
                        "Проверь стабильность интернета и попробуй ещё раз.",
                        "Отключи VPN/прокси или HTTPS-фильтрацию антивируса, если они включены.",
                        "Лаунчер продолжит загрузку с места обрыва при повторном запуске."
                    ],
                    exception);
            }

            return Create(
                "Не удалось записать файл",
                IsDiskFull(ioException)
                    ? "Похоже, на диске закончилось место."
                    : "Файл занят другой программой, диск недоступен или Windows не смогла завершить операцию с файлом.",
                IsDiskFull(ioException)
                    ? [
                        "Освободи место на диске с папкой установки.",
                        "Нужно минимум несколько гигабайт свободного места.",
                        "После очистки нажми «Играть» ещё раз."
                    ]
                    : [
                        "Закрой Minecraft, архиваторы и антивирусные окна проверки.",
                        "Перезагрузи ПК, если файл остался заблокированным.",
                        "Нажми «Проверить целостность» в настройках лаунчера."
                    ],
                exception);
        }

        if (exception is FileNotFoundException)
        {
            return Create(
                "Не найден нужный файл",
                "В установленной сборке отсутствует файл, который нужен для запуска Minecraft или Forge.",
                [
                    "Открой настройки лаунчера.",
                    "Нажми «Проверить целостность».",
                    "Если не поможет, удали папку versions/Forge и запусти установку заново."
                ],
                exception);
        }

        var message = exception.ToString();
        if (message.Contains("Java 17", StringComparison.OrdinalIgnoreCase)
            || message.Contains("java.exe", StringComparison.OrdinalIgnoreCase)
            || message.Contains("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Create(
                "Проблема с Java",
                "Лаунчер не смог найти или запустить Java 17. Обычно встроенная Java ставится автоматически, но установка могла оборваться.",
                [
                    "Запусти лаунчер ещё раз, он попробует установить Java заново.",
                    "Проверь интернет, потому что Java скачивается отдельно.",
                    "Если в настройках указан ручной путь к Java, очисти это поле."
                ],
                exception);
        }

        if (message.Contains("Forge installer failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Forge installer timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("version JSON was not created", StringComparison.OrdinalIgnoreCase))
        {
            return Create(
                "Forge не установился",
                "Установщик Forge завершился с ошибкой или не создал нужные файлы версии.",
                [
                    "Нажми «Играть» ещё раз: лаунчер заново скачает Forge installer при сбое.",
                    "Проверь, не блокирует ли Java/Forge антивирус.",
                    "Открой логи и пришли forge-installer stderr/stdout администратору."
                ],
                exception);
        }

        if (message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase)
            || message.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Хэш не совпал", StringComparison.OrdinalIgnoreCase))
        {
            return Create(
                "Файл скачался неправильно",
                "Контрольная сумма файла не совпала с манифестом. Это защищает от битой или неполной загрузки.",
                [
                    "Нажми «Играть» ещё раз, лаунчер скачает файл заново.",
                    "Если ошибка повторяется у всех, проверь SHA-256 в манифесте на сайте.",
                    "Проверь, что на сервере лежит именно тот архив, который указан в манифесте."
                ],
                exception);
        }

        if (message.Contains("corrupted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot be fully opened as a zip", StringComparison.OrdinalIgnoreCase)
            || message.Contains("InvalidDataException", StringComparison.OrdinalIgnoreCase))
        {
            return Create(
                "Архив сборки повреждён",
                "Скачанный архив не удалось открыть как zip. Обычно это неполная загрузка или повреждённый файл на сервере.",
                [
                    "Запусти установку ещё раз: лаунчер попробует докачать или скачать архив заново.",
                    "Проверь, открывается ли архив на сервере вручную.",
                    "Если ошибка повторяется у всех, перезалей архив сборки."
                ],
                exception);
        }

        if (message.Contains("Недостаточно места", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not enough", StringComparison.OrdinalIgnoreCase))
        {
            return Create(
                "Недостаточно места на диске",
                "На диске с папкой установки не хватает свободного места для архива, Forge и распаковки.",
                [
                    "Освободи минимум 8-10 GB.",
                    "Можно выбрать другой путь установки в настройках.",
                    "После этого нажми «Играть» ещё раз."
                ],
                exception);
        }

        return Create(
            "Неожиданная ошибка лаунчера",
            "Лаунчер столкнулся с ошибкой, для которой пока нет отдельного сценария.",
            [
                "Попробуй повторить действие.",
                "Если ошибка повторяется, открой логи и отправь их администратору.",
                "Можно нажать «Проверить целостность» в настройках."
            ],
            exception);
    }

    private static bool IsDnsError(HttpRequestException exception)
    {
        return exception.InnerException is SocketException socketException
            && socketException.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain;
    }

    // IOException, вызванный обрывом TCP-соединения (SocketException 10054 и т.п.), — это сеть, а не запись на диск.
    private static bool IsNetworkIoError(IOException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException)
            {
                return true;
            }
        }

        var text = exception.ToString();
        return text.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
            || text.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Удаленный хост", StringComparison.OrdinalIgnoreCase)
            || text.Contains("принудительно разорвал", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiskFull(IOException exception)
    {
        const int HResultDiskFull = unchecked((int)0x80070070);
        const int HResultHandleDiskFull = unchecked((int)0x80070027);
        return exception.HResult is HResultDiskFull or HResultHandleDiskFull;
    }

    private static ErrorInfo Create(string title, string summary, IReadOnlyList<string> actions, Exception exception)
    {
        return new ErrorInfo(title, summary, actions, exception.ToString());
    }
}
